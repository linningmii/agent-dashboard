import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { canonicalizeLock, canFallback, installWeb, registries, resolveMode } from './npm-registry.ts';
import type { CommandResult, Run } from './npm-registry.ts';

const ok = { code: 0, output: '' };
const failure = (code: string): CommandResult => ({ code: 1, output: 'npm error code ' + code });
function fixture(t: {after: (fn: () => void) => void}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'registry-test-'));
  fs.mkdirSync(path.join(root, 'web'));
  const lock = JSON.stringify({lockfileVersion:3, packages:{'':{},'node_modules/react':{version:'19.1.1',resolved:registries.npmjs+'react/-/react-19.1.1.tgz',integrity:'sha512-fixture'}}});
  fs.writeFileSync(path.join(root, 'web/package-lock.json'), lock);
  t.after(()=>fs.rmSync(root,{recursive:true,force:true}));
  return {root,lock};
}
test('default is public-first; corporate local override and CLI/env choices are explicit',()=>{
  assert.equal(resolveMode(undefined,undefined,undefined),'auto');
  assert.equal(resolveMode(undefined,undefined,'enzyme'),'enzyme');
  assert.equal(resolveMode(undefined,'npmjs','enzyme'),'npmjs');
  assert.equal(resolveMode('auto','enzyme','enzyme'),'auto');
  assert.throws(()=>resolveMode('unexpected',undefined,undefined));
});
test('public success needs no Enzyme credentials and leaves lockfile untouched',async t=>{
  const {root,lock}=fixture(t); let calls=0;
  const selected=await installWeb({root,npmCli:'npm-cli.js',mode:'auto',env:{ENZYME_NPM_TOKEN:'secret'},log(){},getToken:async()=>{throw Error('Must not authenticate');},run:async(_,args,env)=>{
    calls++; assert.ok(args.includes('--registry='+registries.npmjs));assert.equal(env.ENZYME_NPM_TOKEN,undefined);return ok;
  }});
  assert.equal(selected,'npmjs');assert.equal(calls,1);assert.equal(fs.readFileSync(path.join(root,'web/package-lock.json'),'utf8'),lock);
});
test('network failures retry Enzyme using npm registry-host substitution, once',async t=>{
  const {root}=fixture(t); const tried:string[]=[];
  const selected=await installWeb({root,npmCli:'npm-cli.js',mode:'auto',env:{},log(){},run:async(_,args)=>{
    const registry=args.find(arg=>arg.startsWith('--registry='))!;tried.push(registry);assert.ok(args.includes('--replace-registry-host=npmjs'));
    return tried.length===1?failure('ECONNRESET'):ok;
  }});
  assert.equal(selected,'enzyme');assert.deepEqual(tried,['--registry='+registries.npmjs,'--registry='+registries.enzyme]);
});
test('explicit Enzyme mode never contacts public registry; token reference is scoped and cleaned',async t=>{
  const {root}=fixture(t);let calls=0;let temporaryFile='';let acquired=0;
  await installWeb({root,npmCli:'npm-cli.js',mode:'enzyme',env:{},log(){},getToken:async()=>{acquired++;return 'private-token';},run:async(_,args,env)=>{
    calls++;assert.ok(args.includes('--registry='+registries.enzyme));
    if(calls===1)return failure('E401');
    temporaryFile=args.find(arg=>arg.startsWith('--userconfig='))!.slice('--userconfig='.length);
    const config=fs.readFileSync(temporaryFile,'utf8');assert.ok(config.includes('${ENZYME_NPM_TOKEN}'));assert.equal(config.includes('private-token'),false);
    assert.equal(env.ENZYME_NPM_TOKEN,'private-token');return ok;
  }});
  assert.equal(calls,2);assert.equal(acquired,1);assert.equal(fs.existsSync(temporaryFile),false);
});
test('fallback does not mask dependency, missing-version, or integrity failures',async t=>{
  const {root}=fixture(t);
  for(const code of ['ERESOLVE','EINTEGRITY','E404','ETARGET','EACCES','EUSAGE','E401']){
    let calls=0;
    await assert.rejects(installWeb({root,npmCli:'npm-cli.js',mode:'auto',env:{},log(){},run:async()=>{calls++;return failure(code);}}));assert.equal(calls,1);
  }
  assert.equal(canFallback({code:1,output:'npm error code EINTEGRITY\nnpm error code ECONNRESET'}),false);
  assert.equal(canFallback(failure('EPROTO')),true);
  assert.equal(canFallback(failure('E403')),true);
});
test('public-only mode never falls back; failed authenticated attempt redacts and cleans',async t=>{
  const {root}=fixture(t);let calls=0;
  await assert.rejects(installWeb({root,npmCli:'npm-cli.js',mode:'npmjs',env:{},log(){},run:async()=>{calls++;return failure('ECONNRESET');}}));assert.equal(calls,1);
  let file='';
  await assert.rejects(installWeb({root,npmCli:'npm-cli.js',mode:'enzyme',env:{ENZYME_NPM_TOKEN:'private-token'},getToken:async()=> 'private-token',log(){},run:async(_,args)=>{
    file=args.find(arg=>arg.startsWith('--userconfig='))!.slice('--userconfig='.length);return {code:1,output:'npm error code ERESOLVE private-token'};
  }}),error=>error instanceof Error && !error.message.includes('private-token'));
  assert.equal(fs.existsSync(file),false);
});
test('portable lock normalization retains pinned versions and integrity and leaves other sources alone',()=>{
  const input={lockfileVersion:3,packages:{'node_modules/@scope/name':{version:'1.2.3',resolved:registries.enzyme+'@scope/name/-/name-1.2.3.tgz',integrity:'sha512-fixed'},other:{resolved:'https://elsewhere.example/a.tgz',integrity:'sha512-other'}}};
  const normalized=JSON.parse(canonicalizeLock(JSON.stringify(input)));
  assert.equal(normalized.packages['node_modules/@scope/name'].resolved,registries.npmjs+'@scope/name/-/name-1.2.3.tgz');
  assert.equal(normalized.packages['node_modules/@scope/name'].version,'1.2.3');assert.equal(normalized.packages['node_modules/@scope/name'].integrity,'sha512-fixed');
  assert.deepEqual(normalized.packages.other,input.packages.other);
});

test('fallback authenticates only at Enzyme after public access is denied',async t=>{
  const {root}=fixture(t); const attempts:string[]=[]; let tokenRequests=0;
  await installWeb({root,npmCli:'npm-cli.js',mode:'auto',env:{},log(){},getToken:async()=>{tokenRequests++;return 'test-token';},run:async(_,args,env)=>{
    attempts.push(args.find(arg=>arg.startsWith('--registry='))!);
    if(attempts.length===1){assert.equal(tokenRequests,0);return failure('E403');}
    if(attempts.length===2){assert.equal(tokenRequests,0);return failure('E401');}
    assert.equal(env.ENZYME_NPM_TOKEN,'test-token');return ok;
  }});
  assert.deepEqual(attempts,['--registry='+registries.npmjs,'--registry='+registries.enzyme,'--registry='+registries.enzyme]);
  assert.equal(tokenRequests,1);
});

test('lock updates select npm install and normalize successful Enzyme output',async t=>{
  const {root}=fixture(t);const lockPath=path.join(root,'web/package-lock.json');
  const updated={lockfileVersion:3,packages:{'node_modules/react':{version:'19.1.1',integrity:'sha512-pinned',resolved:registries.enzyme+'react/-/react-19.1.1.tgz'}}};
  await installWeb({root,npmCli:'npm-cli.js',mode:'enzyme',updateLock:true,env:{},log(){},run:async(_,args,_env,cwd)=>{
    assert.equal(cwd,path.join(root,'web'));
    assert.equal(args[1],'install');fs.writeFileSync(lockPath,JSON.stringify(updated));return ok;
  }});
  const item=JSON.parse(fs.readFileSync(lockPath,'utf8')).packages['node_modules/react'];
  assert.deepEqual(item,{...updated.packages['node_modules/react'],resolved:registries.npmjs+'react/-/react-19.1.1.tgz'});
});

test('missing lockfile fails before running npm or acquiring credentials',async t=>{
  const {root}=fixture(t);fs.unlinkSync(path.join(root,'web/package-lock.json'));
  await assert.rejects(installWeb({root,npmCli:'npm-cli.js',mode:'auto',env:{},log(){},run:async()=>assert.fail('Must not run npm'),getToken:async()=>assert.fail('Must not authenticate')}),/Missing web/);
});

test('spawn failure still removes the temporary authentication configuration',async t=>{
  const {root}=fixture(t);let file='';
  await assert.rejects(installWeb({root,npmCli:'npm-cli.js',mode:'enzyme',env:{ENZYME_NPM_TOKEN:'test-token'},log(){},run:async(_,args)=>{
    file=args.find(arg=>arg.startsWith('--userconfig='))!.slice('--userconfig='.length);
    assert.ok(fs.existsSync(file));throw new Error('Could not start npm');
  }}),/Could not start npm/);
  assert.equal(fs.existsSync(file),false);
});
