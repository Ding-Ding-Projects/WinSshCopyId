import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { deflateSync } from 'node:zlib';
import { verifyNativeEvidence } from '../../scripts/verify-native-evidence.mjs';

// Synthetic validation fixtures only. These bytes are never product evidence.
const hash = b => createHash('sha256').update(b).digest('hex');
const crc = b => { let v=0xffffffff; for(const n of b){v^=n;for(let i=0;i<8;i++)v=(v>>>1)^((v&1)?0xedb88320:0);}return (v^0xffffffff)>>>0; };
function png() {
  const chunk=(name,data)=>{const n=Buffer.from(name),out=Buffer.alloc(data.length+12);out.writeUInt32BE(data.length);n.copy(out,4);data.copy(out,8);out.writeUInt32BE(crc(Buffer.concat([n,data])),data.length+8);return out;};
  const ihdr=Buffer.alloc(13);ihdr.writeUInt32BE(1);ihdr.writeUInt32BE(1,4);ihdr[8]=8;ihdr[9]=2;
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',ihdr),chunk('IDAT',deflateSync(Buffer.from([0,42,87,130]))),chunk('IEND',Buffer.alloc(0))]);
}
function fixture() {
  const root=mkdtempSync(join(tmpdir(),'native-evidence-test-')),repo=join(root,'repo'),run=join(root,'run');mkdirSync(repo);mkdirSync(run);mkdirSync(join(repo,'src'));mkdirSync(join(repo,'docs'));mkdirSync(join(repo,'docs','evidence'));
  const write=(base,path,value)=>writeFileSync(join(base,path),typeof value==='string'||Buffer.isBuffer(value)?value:JSON.stringify(value));
  const git=args=>execFileSync('git',['-C',repo,...args],{encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
  git(['init','-q']);git(['config','user.name','Claude Fable 5.1']);git(['config','user.email','noreply@anthropic.com']);write(repo,'src/product.txt','fixture');git(['add','src/product.txt']);git(['commit','-qm','Create validation fixture\n\nA one-pixel fixture keeps the checks honest.\n一粒像素，驗證都要認真。\n\nCo-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>']);const commit=git(['rev-parse','HEAD']);
  const now=Date.now(),iso=n=>new Date(now+n).toISOString(),image=png(),imageHash=hash(image),artifact=Buffer.from('synthetic executable'),artifactHash=hash(artifact);
  write(repo,'application.exe',artifact);write(run,'raw.png',image);write(repo,'docs/evidence/capture.png',image);
  const sidecar=(name,value)=>{write(run,name,value);return {path:name,sha256:hash(readFileSync(join(run,name)))};};
  const entry={path:'application.exe',sha256:artifactHash.toUpperCase(),bytes:artifact.length};
  const original=sidecar('build.json',{sourceCommit:commit,createdUtc:iso(-10000),files:[entry]});
  const binding=sidecar('binding.json',{kind:'derived-build-binding',sourceCommit:commit,artifactSha256:artifactHash,originalReceipt:original,artifactEntry:entry});
  const identity=sidecar('identity.json',{pid:1234,hwnd:'0x123',sourceCommit:commit,artifactSha256:artifactHash,creationDate:iso(-8000),executablePath:'fixture/application.exe',desktop:'fixture-hidden'});
  const interaction=sidecar('interaction.json',{id:'settings-invoked',pid:1234,hwnd:'0x123',sourceCommit:commit,captureSha256:imageHash,observed:true,steps:[{action:'InvokePattern',target:'Settings',observedResult:'Settings visible',ok:true}]});
  const diagnostics=sidecar('diagnostics.json',{kind:'winui-uia-and-application-events',pid:1234,sourceCommit:commit,querySucceeded:true,browserObservation:'not-applicable',observedCrashCount:0,providers:['Application Error','.NET Runtime'],startedAt:iso(-8000),completedAt:iso(-1000)});
  const inspection=sidecar('inspection.json',{captureSha256:imageHash,reviewer:'synthetic-test-only',decoded:true,pixelsInspected:true,expectedStateVisible:true,sensitiveDataReviewed:true,visibleDesktopUntouched:true,expectedSurfaceOnly:true,unrelatedTargetsObserved:false,mocked:false,handEdited:false});
  const cleanup=sidecar('cleanup.json',{pid:1234,hwnd:'0x123',desktop:'fixture-hidden',ownedOnly:true,appAbsent:true,helpersAbsent:true,desktopAbsent:true,observedAt:iso(-1000)});
  const receipt={version:1,kind:'winui-native-window',route:'direct-cheap-cli-with-uia-helper',id:'settings-native-test',source:{startCommit:commit,endCommit:commit,startedAt:iso(-9000),completedAt:iso(-500),cleanAtStart:true,cleanAtEnd:true,artifactPath:'application.exe',artifactSha256:artifactHash,buildBinding:binding},capture:{rawPath:'raw.png',promotedPath:'docs/evidence/capture.png',sha256:imageHash,rawSha256:imageHash,mimeType:'image/png',bytes:image.length,width:1,height:1,startedAt:iso(-3000),capturedAt:iso(-2000)},state:{screen:'Settings',state:'saved',theme:'light',language:'English',captureKind:'window',viewport:{width:1,height:1,scale:1}},runtime:{launchPid:1234,hwnd:'0x123',hwndResolvedLive:true,geometry:{dpi:96,windowWidth:1,windowHeight:1},diagnosticKind:'winui-uia-and-application-events',identity,interaction,interactionProofId:'settings-invoked',diagnostics,cleanup},inspection,inventory:{path:'docs/evidence/inventory.json',recordId:'settings-native-test'},documentation:[{path:'README.md',alt:'Synthetic settings fixture'},{path:'docs/article.md',alt:'Synthetic settings fixture'}]};
  write(repo,'docs/evidence/inventory.json',{records:[{id:receipt.id,active:true,path:receipt.capture.promotedPath,sourceCommit:commit,artifactSha256:artifactHash,captureSha256:imageHash,screen:'Settings',state:'saved',theme:'light',language:'English',viewportWidth:1,viewportHeight:1,scale:1,interactionProofId:'settings-invoked',interactionReceiptSha256:interaction.sha256,inspectionStatus:'inspected'}]});
  write(repo,'README.md','![Synthetic settings fixture](docs/evidence/capture.png)');write(repo,'docs/article.md','![Synthetic settings fixture](evidence/capture.png)');
  const save=()=>write(run,'receipt.json',receipt);save();
  return {root,repo,run,receipt,save,write,sidecar,options:{receiptPath:'receipt.json',repoRoot:repo,runRoot:run,expectedCommit:commit,now}};
}
test('native receipt accepts a complete synthetic fixture and resolves nested documentation links',()=>{const f=fixture();try{assert.equal(verifyNativeEvidence(f.options).valid,true);}finally{rmSync(f.root,{recursive:true,force:true});}});
const negatives=[
 ['missing image',f=>rmSync(join(f.run,'raw.png')),/ENOENT/],
 ['changed image',f=>f.write(f.run,'raw.png',Buffer.from('changed')),/Capture bytes changed/],
 ['changed source',f=>{f.receipt.source.endCommit='a'.repeat(40);f.save();},/Source changed/],
 ['changed product',f=>f.write(f.repo,'src/product.txt','different'),/Product source differs/],
 ['changed sidecar',f=>f.write(f.run,'interaction.json','{}'),/Sidecar hash mismatch/],
 ['missing sidecar',f=>rmSync(join(f.run,'inspection.json')),/ENOENT/],
 ['stale evidence',f=>{f.options.now+=169*3600000;},/Stale/],
 ['browser diagnostic substitution',f=>{f.receipt.runtime.pageErrorCount=0;f.save();},/Browser fields/],
 ['wrong geometry',f=>{f.receipt.runtime.geometry.dpi=144;f.save();},/Viewport/],
 ['commented documentation',f=>f.write(f.repo,'README.md','<!-- ![Synthetic settings fixture](docs/evidence/capture.png) -->'),/documentation image link/],
 ['escaped raw path',f=>{f.write(f.root,'outside.png',png());f.receipt.capture.rawPath='../outside.png';f.save();},/escapes owned root/],
 ['missing cleanup',f=>{f.receipt.runtime.cleanup=f.sidecar('cleanup-bad.json',{pid:1234,hwnd:'0x123',desktop:'fixture-hidden',ownedOnly:true,appAbsent:false});f.save();},/Incomplete owned cleanup/],
 ['wrong original build entry',f=>{const original=f.sidecar('wrong-build.json',{sourceCommit:f.options.expectedCommit,createdUtc:new Date(f.options.now-10000).toISOString(),files:[]});const b=JSON.parse(readFileSync(join(f.run,'binding.json')));b.originalReceipt=original;f.receipt.source.buildBinding=f.sidecar('wrong-binding.json',b);f.save();},/Build file entry/]
];
for(const [name,mutate,pattern] of negatives)test(`rejects ${name}, then accepts restored fixture`,()=>{const f=fixture();try{assert.equal(verifyNativeEvidence(f.options).valid,true);mutate(f);assert.throws(()=>verifyNativeEvidence(f.options),pattern);}finally{rmSync(f.root,{recursive:true,force:true});}const fresh=fixture();try{assert.equal(verifyNativeEvidence(fresh.options).valid,true);}finally{rmSync(fresh.root,{recursive:true,force:true});}});
test('atomic staging rejects wrong hashes and preserves exact original bytes on rollback',()=>{const f=fixture();try{const stage=resolve('scripts/stage-native-evidence.mjs');const run=args=>execFileSync(process.execPath,[stage,...args],{stdio:'pipe'});const base=['--repo-root',f.repo,'--run-root',f.run];assert.throws(()=>run(['--mode','stage',...base,'--raw','raw.png','--target','docs/evidence/new.png','--expected-raw-sha256','0'.repeat(64),'--transaction','transaction.json']));run(['--mode','stage',...base,'--raw','raw.png','--target','docs/evidence/new.png','--expected-raw-sha256',f.receipt.capture.sha256,'--transaction','transaction.json']);assert.deepEqual(readFileSync(join(f.repo,'docs/evidence/new.png')),readFileSync(join(f.run,'raw.png')));run(['--mode','rollback',...base,'--transaction','transaction.json']);assert.throws(()=>readFileSync(join(f.repo,'docs/evidence/new.png')),/ENOENT/);}finally{rmSync(f.root,{recursive:true,force:true});}});
