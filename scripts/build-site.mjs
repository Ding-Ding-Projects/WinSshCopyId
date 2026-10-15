import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const out=path.join(root,'artifacts','site');
const walk=d=>fs.existsSync(d)?fs.readdirSync(d,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(d,e.name)):[path.join(d,e.name)]):[];
export function collectDocuments(base=root){
 return ['docs/articles','docs/features','docs/wiki'].flatMap(dir=>walk(path.join(base,dir))).filter(p=>p.endsWith('.md')).sort().map(p=>{
  const source=path.relative(base,p).replaceAll('\\','/');const markdown=fs.readFileSync(p,'utf8').replace(/^\uFEFF/,'');
  return {id:source.slice(0,-3),source,title:/^#\s+(.+)$/m.exec(markdown)?.[1]??path.basename(p),category:source.split('/')[1],markdown};
 });
}
export function validateRelease(value){
 if(!value)return null;
 if(value.platform!=='win-x64'||value.verificationState!=='verified')throw Error('Release platform or verification state unavailable.');
 const keys=['schemaVersion','version','sourceCommit','tag','publishedAt','platform','verificationState','releaseUrl','isDraft','isPrerelease','assets'];
 if(Object.keys(value).some(k=>!keys.includes(k))||value.schemaVersion!==1||value.isDraft!==false||value.isPrerelease!==false||!/^\d+\.\d+\.\d+$/.test(value.version)||! /^[a-f0-9]{40}$/.test(value.sourceCommit)||!Number.isFinite(Date.parse(value.publishedAt))||!Array.isArray(value.assets))throw Error('Invalid release manifest.');
 if(!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(value.tag))throw Error('Invalid release tag.');
 if(value.releaseUrl!==`https://github.com/Ding-Ding-Projects/WinSshCopyId/releases/tag/${value.tag}`)throw Error('Release URL is not immutable.');
 const setup=value.assets.find(a=>a.name==='Setup.exe');
 if(!setup||!Number.isSafeInteger(setup.size)||setup.size<1||!/^[a-f0-9]{64}$/.test(setup.sha256))throw Error('Missing verified installer metadata.');
 const expected=`https://github.com/Ding-Ding-Projects/WinSshCopyId/releases/download/${value.tag}/Setup.exe`;
 if(setup.url!==expected)throw Error('Installer URL is not immutable project release URL.');
 return value;
}
export function build(){
 const vendor=JSON.parse(fs.readFileSync(path.join(root,'site/vendor/manifest.json'),'utf8'));
 const actual=crypto.createHash('sha256').update(fs.readFileSync(path.join(root,'site/vendor/material.js'))).digest('hex');
 if(vendor.package!=='@material/web'||vendor.version!=='2.5.0'||actual!==vendor.sha256)throw Error('Bundled component identity or SHA-256 mismatch.');
 const manifest=JSON.parse(fs.readFileSync(path.join(root,'build/manifest.json'),'utf8').replace(/^\uFEFF/,''));
 const documents=collectDocuments();
 const sourceCommit=execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
 const sourceUpdatedAt=execFileSync('git',['show','-s','--format=%cI','HEAD'],{cwd:root,encoding:'utf8'}).trim();
 const releasePath=path.join(root,'site/release-manifest.json');
 const release=validateRelease(fs.existsSync(releasePath)?JSON.parse(fs.readFileSync(releasePath,'utf8')):null);
 const coverage=JSON.parse(fs.readFileSync(path.join(root,'site/feature-ledger.json'),'utf8').replace(/^\uFEFF/,''));
 fs.mkdirSync(out,{recursive:true});
 for(const p of walk(path.join(root,'site')).filter(p=>!p.includes(path.sep+'node_modules'+path.sep)&&!['release-manifest.json','feature-ledger.json','package.json','package-lock.json','components-entry.mjs','bundle-components.mjs'].includes(path.basename(p)))){
  const destination=path.join(out,path.relative(path.join(root,'site'),p));fs.mkdirSync(path.dirname(destination),{recursive:true});fs.copyFileSync(p,destination);
 }
 const data={schemaVersion:1,product:manifest.product,version:manifest.version,sourceCommit,sourceUpdatedAt,release,documents,coverage};
 fs.writeFileSync(path.join(out,'content.js'),`globalThis.SITE_CONTENT=${JSON.stringify(data).replaceAll('<','\\u003c')};\n`);
 fs.writeFileSync(path.join(out,'.nojekyll'),'');
 fs.writeFileSync(path.join(out,'content-inventory.json'),JSON.stringify({sourceCommit,documents:documents.map(({id,source,title,markdown})=>({id,source,title,route:'#article/'+id,anchors:[...markdown.matchAll(/^#{1,6}\s+(.+)$/gm)].map(m=>m[1].toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu,'').trim().replace(/\s+/g,'-'))}))},null,2));
 console.log(`Documentation output: ${documents.length} complete articles and wiki chapters. Source ${sourceCommit}. Release ${release?'manifest present':'unavailable'}.`);
 return data;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url))build();


