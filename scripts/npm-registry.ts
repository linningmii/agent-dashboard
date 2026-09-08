import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn } from 'node:child_process';

export const registries = {
  npmjs: 'https://registry.npmjs.org/',
  enzyme: 'https://o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/',
} as const;
export type Registry = keyof typeof registries;
export type Mode = Registry | 'auto';
export type CommandResult = { code: number; output: string };
export type Run = (command: string, args: string[], env: NodeJS.ProcessEnv) => Promise<CommandResult>;

export function registryMode(value: unknown): Mode {
  if (value === 'auto' || value === 'npmjs' || value === 'enzyme') return value;
  throw new Error('Registry must be auto, npmjs, or enzyme.');
}

export function resolveMode(explicit: string | undefined, environment: string | undefined, local: unknown): Mode {
  return registryMode(explicit ?? environment ?? local ?? 'auto');
}

// Retry feed availability failures only. Dependency, integrity, permission, and build
// failures must not silently change the source of a package.
export function canFallback(result: CommandResult): boolean {
  const codes = [...result.output.matchAll(/(?:npm\s+(?:error|ERR!)\s+code\s+)([A-Z0-9_]+)/gi)].map(match => match[1]!.toUpperCase());
  if (codes.some(code => ['ERESOLVE', 'EINTEGRITY', 'E404', 'ETARGET', 'EACCES', 'EPERM', 'EUSAGE', 'E401'].includes(code))) return false;
  return codes.some(code => ['EAI_AGAIN', 'ENOTFOUND', 'ECONNREFUSED', 'ECONNRESET', 'ETIMEDOUT', 'ENETUNREACH',
    'EHOSTUNREACH', 'EPROTO', 'ERR_SSL_SSLV3_ALERT_HANDSHAKE_FAILURE', 'UNABLE_TO_VERIFY_LEAF_SIGNATURE',
    'UNABLE_TO_GET_ISSUER_CERT_LOCALLY', 'SELF_SIGNED_CERT_IN_CHAIN', 'CERT_HAS_EXPIRED', 'E403', 'E429',
    'E500', 'E502', 'E503', 'E504', 'ERR_SOCKET_TIMEOUT'].includes(code));
}

export function canonicalizeLock(text: string): string {
  const lock = JSON.parse(text) as { packages?: Record<string, { resolved?: string }> };
  if (!lock.packages) throw new Error('Expected an npm v3/v2 lockfile with packages.');
  for (const item of Object.values(lock.packages)) {
    if (item.resolved?.startsWith(registries.enzyme)) item.resolved = registries.npmjs + item.resolved.slice(registries.enzyme.length);
  }
  return JSON.stringify(lock, null, 2) + '\n';
}

export function findNpmCli(): string {
  const candidates = [process.env.npm_execpath, path.join(path.dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js')];
  for (const directory of (process.env.PATH || '').split(path.delimiter)) {
    const executable = path.join(directory, process.platform === 'win32' ? 'npm.cmd' : 'npm');
    if (!fs.existsSync(executable)) continue;
    const real = fs.realpathSync(executable);
    if (real.endsWith('npm-cli.js')) candidates.push(real);
    candidates.push(path.join(path.dirname(real), 'node_modules/npm/bin/npm-cli.js'));
    candidates.push(path.join(path.dirname(real), '../lib/node_modules/npm/bin/npm-cli.js'));
  }
  const cli = candidates.find(candidate => candidate && fs.existsSync(candidate));
  if (!cli) throw new Error('npm CLI was not found. Install Node.js with npm or use npm run setup:web.');
  return cli;
}

export const runCommand: Run = (command, args, env) => new Promise((resolve, reject) => {
  const child = spawn(command, args, { env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  let output = '';
  const collect = (chunk: Buffer) => { output = (output + chunk.toString()).slice(-32768); };
  child.stdout.on('data', collect); child.stderr.on('data', collect);
  child.once('error', reject);
  child.once('close', code => resolve({ code: code ?? 1, output }));
  const interrupt = () => child.kill();
  process.once('SIGINT', interrupt); process.once('SIGTERM', interrupt);
  child.once('close', () => { process.removeListener('SIGINT', interrupt); process.removeListener('SIGTERM', interrupt); });
});

// Windows .cmd files require a shell. Azure CLI's official MSI also has a Python
// entry point, allowing us to keep process arguments out of shell command strings.
function azureCommand(): { command: string; args: string[] } | null {
  for (const directory of (process.env.PATH || '').split(path.delimiter)) {
    if (process.platform === 'win32') {
      const python = path.resolve(directory, '..', 'python.exe');
      if (fs.existsSync(path.join(directory, 'az.cmd')) && fs.existsSync(python)) return { command: python, args: ['-IBm', 'azure.cli'] };
      if (fs.existsSync(path.join(directory, 'az.exe'))) return { command: path.join(directory, 'az.exe'), args: [] };
    } else if (fs.existsSync(path.join(directory, 'az'))) return { command: path.join(directory, 'az'), args: [] };
  }
  return null;
}

export async function acquireEnzymeToken(run: Run, env: NodeJS.ProcessEnv): Promise<string> {
  if (env.ENZYME_NPM_TOKEN) return env.ENZYME_NPM_TOKEN;
  const cli = azureCommand();
  if (!cli) throw new Error('Enzyme requires feed access. Configure npm credentials or set ENZYME_NPM_TOKEN; alternatively install Azure CLI and run az login.');
  const result = await run(cli.command, [...cli.args, 'account', 'get-access-token', '--resource', '499b84ac-1321-427f-aa17-267ca6975798', '--query', 'accessToken', '-o', 'tsv'], env);
  if (result.code || !result.output.trim()) throw new Error('Enzyme authentication failed. Run az login or supply valid feed credentials.');
  return result.output.trim();
}

export async function installWeb(options: {
  root: string; npmCli: string; mode: Mode; updateLock?: boolean;
  env?: NodeJS.ProcessEnv; run?: Run; getToken?: () => Promise<string>; log?: (message: string) => void;
}): Promise<Registry> {
  const { root, npmCli, mode, updateLock = false, run = runCommand, log = console.log } = options;
  const env = { ...(options.env ?? process.env) };
  const web = path.join(root, 'web');
  const lockPath = path.join(web, 'package-lock.json');
  if (!fs.existsSync(lockPath) && !updateLock) throw new Error('Missing web/package-lock.json. Use --update-lock to resolve dependencies intentionally.');
  let temporaryDirectory: string | undefined;
  let secret = env.ENZYME_NPM_TOKEN;
  async function attempt(registry: Registry, authenticated = false): Promise<CommandResult> {
    log('Installing locked dependencies from ' + registry + '…');
    const commandEnv = { ...env };
    const args = [npmCli, updateLock ? 'install' : 'ci', '--prefix', web, '--registry=' + registries[registry],
      '--replace-registry-host=npmjs', '--ignore-scripts', '--no-audit', '--no-fund', '--update-notifier=false', '--fetch-retries=0', '--fetch-timeout=20000', '--loglevel=error'];
    if (authenticated) {
      secret = await (options.getToken ?? (() => acquireEnzymeToken(run, env)))();
      temporaryDirectory ??= fs.mkdtempSync(path.join(os.tmpdir(), 'agent-dashboard-npm-'));
      const file = path.join(temporaryDirectory, 'user.npmrc');
      // The file contains an environment reference, never the credential value.
      fs.writeFileSync(file, '//o365exchange.pkgs.visualstudio.com/_packaging/Enzyme/npm/registry/:_authToken=${ENZYME_NPM_TOKEN}\n', { mode: 0o600 });
      commandEnv.ENZYME_NPM_TOKEN = secret;
      args.push('--userconfig=' + file);
    } else if (registry === 'npmjs') {
      // Corporate token is irrelevant to a public install and must not be inherited.
      delete commandEnv.ENZYME_NPM_TOKEN;
    }
    return run(process.execPath, args, commandEnv);
  }
  function failure(result: CommandResult): Error {
    let output = result.output;
    if (secret) output = output.replaceAll(secret, '[redacted]');
    output = output.replace(/(Bearer\s+)[^\s]+/gi, '$1[redacted]').replace(/(https?:\/\/)[^/\s]+@/g, '$1[redacted]@');
    return new Error('Package installation failed (exit code ' + result.code + ').\n' + output.slice(-4000));
  }
  try {
    let registry: Registry = mode === 'enzyme' ? 'enzyme' : 'npmjs';
    let result = await attempt(registry, registry === 'enzyme' && Boolean(secret));
    if (result.code && mode === 'auto' && canFallback(result)) {
      log('npmjs is unavailable or denied. Trying the authenticated Enzyme backup; TLS checks remain enabled.');
      registry = 'enzyme'; result = await attempt(registry, Boolean(secret));
    }
    if (result.code && registry === 'enzyme' && /(?:npm\s+(?:error|ERR!)\s+code\s+)E40[13]\b/i.test(result.output)) {
      log('Refreshing Enzyme authentication using your configured token or Azure CLI sign-in.');
      result = await attempt('enzyme', true);
    }
    if (result.code) throw failure(result);
    if (updateLock) fs.writeFileSync(lockPath, canonicalizeLock(fs.readFileSync(lockPath, 'utf8')));
    log('Dependencies installed from ' + registry + '.');
    return registry;
  } finally {
    if (temporaryDirectory) fs.rmSync(temporaryDirectory, { recursive: true, force: true });
  }
}
