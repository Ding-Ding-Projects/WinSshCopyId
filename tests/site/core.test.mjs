import test from 'node:test';
import assert from 'node:assert/strict';
import {validateWording,renderMarkdown,resolveDocumentLink} from '../../site/core.mjs';
import {validateRelease,collectDocuments} from '../../scripts/build-site.mjs';
test('wording validates whole payload and rejects duplicate or unsafe keys',()=>{
 assert.equal(validateWording('{"schemaVersion":1,"entries":{"Read":"Browse"}}').entries.Read,'Browse');
 for(const text of ['{"schemaVersion":1,"entries":{"Read":"a","Read":"b"}}','{"schemaVersion":1,"schemaVersion":1,"entries":{}}','{"schemaVersion":1,"entries":{"__proto__":"x"}}','{"schemaVersion":2,"entries":{}}','{"schemaVersion":1,"entries":{},"extra":1}','{"schemaVersion":1,"entries":{"x":2}}'])assert.throws(()=>validateWording(text));
 assert.throws(()=>validateWording(JSON.stringify({schemaVersion:1,entries:{['x'.repeat(161)]:'a'}})));
 assert.throws(()=>validateWording(JSON.stringify({schemaVersion:1,entries:{x:'a'.repeat(1001)}})));
 assert.throws(()=>validateWording(' '.repeat(262145)));
});
test('renderer escapes active content and keeps internal article navigation',()=>{
 const docs=[{id:'docs/articles/a',markdown:'# A\n\n<script>alert(1)</script>\n\n[Next](b.md)\n\n[Unsafe](javascript:alert)'},{id:'docs/articles/b',markdown:'# B'}];
 const html=renderMarkdown(docs[0],docs);assert.ok(!html.includes('<script>'));assert.ok(html.includes('href="#article/docs/articles/b"'));assert.ok(!html.includes('href="javascript:'));
 assert.equal(resolveDocumentLink('docs/articles/a','../wiki/Home.md',[{id:'docs/wiki/Home'}]),'#article/docs/wiki/Home');
});
test('release control requires immutable installer evidence',()=>{
 assert.equal(validateRelease(null),null);assert.throws(()=>validateRelease({}));
 const value={schemaVersion:1,platform:'win-x64',verificationState:'verified',releaseUrl:'https://github.com/Ding-Ding-Projects/WinSshCopyId/releases/tag/v1.2.3',version:'1.2.3',sourceCommit:'a'.repeat(40),tag:'v1.2.3',publishedAt:'2026-10-14T00:00:00Z',isDraft:false,isPrerelease:false,assets:[{name:'Setup.exe',size:42,sha256:'b'.repeat(64),url:'https://github.com/Ding-Ding-Projects/WinSshCopyId/releases/download/v1.2.3/Setup.exe'}]};
 assert.equal(validateRelease(value),value);assert.throws(()=>validateRelease({...value,isDraft:true}));assert.throws(()=>validateRelease({...value,assets:[{...value.assets[0],url:'https://example.com/Setup.exe'}]}));
});
test('complete articles and wiki are collected, with each local article link resolvable',()=>{
 const docs=collectDocuments();assert.ok(docs.some(d=>d.id==='docs/wiki/Home'));assert.ok(docs.some(d=>d.id==='docs/articles/getting-started/first-session'));
 for(const doc of docs)for(const match of doc.markdown.matchAll(/\[[^\]]+\]\(([^)]+\.md(?:#[^)]*)?)\)/g))assert.ok(resolveDocumentLink(doc.id,match[1],docs),`${doc.source}: ${match[1]}`);
});

