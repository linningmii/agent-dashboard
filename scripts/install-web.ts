import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { findNpmCli, installWeb, resolveMode } from './npm-registry.ts';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
if (args.includes('--help')) {
  console.log('Usage: npm run setup:web -- [--registry auto|npmjs|enzyme] [--update-lock]\nDefault: npm ci, npmjs with Enzyme fallback. AGENT_NPM_REGISTRY or ignored npm-install.local.json can select Enzyme without contacting npmjs.');
} else {
  try {
    let explicit: string | undefined;
    let updateLock = false;
    for (let i = 0; i < args.length; i++) {
      const arg = args[i]!;
      if (arg === '--registry') { explicit = args[++i]; if (!explicit) throw new Error('--registry requires a value'); }
      else if (arg.startsWith('--registry=')) explicit = arg.slice('--registry='.length);
      else if (arg === '--update-lock') updateLock = true;
      else if (arg !== '--clean') throw new Error('Unknown option: ' + arg);
    }
    const localPath = path.join(root, 'npm-install.local.json');
    const local = fs.existsSync(localPath) ? (JSON.parse(fs.readFileSync(localPath, 'utf8')) as { registry?: unknown }).registry : undefined;
    const mode = resolveMode(explicit, process.env.AGENT_NPM_REGISTRY, local);
    await installWeb({ root, npmCli: findNpmCli(), mode, updateLock });
  } catch (error) { console.error(error instanceof Error ? error.message : 'Installation failed.'); process.exitCode = 1; }
}
