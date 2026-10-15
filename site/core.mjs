// Bounded strict JSON reader, including duplicate-key rejection before object creation.
export function parseStrictJson(text){
 if(new TextEncoder().encode(text).length>262144)throw Error('File exceeds 256 KiB.');
 let i=0;const ws=()=>{while(/[\t\n\r ]/.test(text[i]??'')&&i<text.length)i++;};
 function value(depth){
  ws();if(depth>8)throw Error('Maximum nesting depth exceeded.');
  if(text[i]==='"')return string();
  if(text[i]==='{'){
   i++;const result=Object.create(null);ws();if(text[i]==='}'){i++;return result;}
   while(true){ws();const key=string();if(['__proto__','prototype','constructor'].includes(key)||Object.hasOwn(result,key))throw Error('Duplicate or unsafe property.');ws();if(text[i++]!==':')throw Error('Expected colon.');result[key]=value(depth+1);ws();const c=text[i++];if(c==='}')return result;if(c!==',')throw Error('Expected object separator.');}
  }
  if(text[i]==='['){i++;const a=[];ws();if(text[i]===']'){i++;return a;}while(true){a.push(value(depth+1));ws();const c=text[i++];if(c===']')return a;if(c!==',')throw Error('Expected array separator.');}}
  const match=/^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/.exec(text.slice(i));if(!match)throw Error('Invalid JSON value.');i+=match[0].length;return JSON.parse(match[0]);
 }
 function string(){if(text[i]!=='"')throw Error('Expected string.');const start=i++;while(i<text.length){if(text[i]==='\\'){i+=2;continue;}if(text[i++]==='"')return JSON.parse(text.slice(start,i));}throw Error('Unclosed string.');}
 const result=value(0);ws();if(i!==text.length)throw Error('Trailing JSON content.');return result;
}
export function validateWording(text){
 const obj=parseStrictJson(text);
 if(!obj||Array.isArray(obj)||Object.keys(obj).sort().join(',')!=='entries,schemaVersion'||obj.schemaVersion!==1||!obj.entries||Array.isArray(obj.entries)||typeof obj.entries!=='object')throw Error('Unsupported local wording schema.');
 for(const [key,val]of Object.entries(obj.entries))if(typeof val!=='string'||[...key].length<1||[...key].length>160||[...val].length>1000||/[\u0000-\u001f\u007f-\u009f]/.test(key+val)||/[\ud800-\udfff]/u.test(key+val))throw Error('Invalid replacement entry.');
 return obj;
}
export const escapeHtml=s=>String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'",'&#39;');
export function resolveDocumentLink(from,href,documents){
 if(/^https:\/\//.test(href))return href;
 if(href.startsWith('#'))return `#article/${from}~${href.slice(1)}`;
 const [file,anchor]=href.split('#');const parts=from.split('/');parts.pop();
 for(const part of file.split('/')){if(part==='..')parts.pop();else if(part!=='.'&&part)parts.push(part);}
 const id=parts.join('/').replace(/\.md$/,'');
 return documents.some(d=>d.id===id)?`#article/${id}${anchor?'~'+anchor:''}`:null;
}
export function headingId(text){return text.toLowerCase().replace(/[^\p{L}\p{N}\s-]/gu,'').trim().replace(/\s+/g,'-');}
export function renderMarkdown(doc,documents){
 const inline=text=>{
  const tokens=[];const stash=html=>`\u0000${tokens.push(html)-1}\u0000`;
  let s=text.replace(/`([^`]+)`/g,(_,v)=>stash(`<code>${escapeHtml(v)}</code>`));
  s=s.replace(/\[([^\]]+)\]\(([^)]+)\)/g,(_,label,href)=>{const url=resolveDocumentLink(doc.id,href,documents);return stash(url?`<a href="${escapeHtml(url)}"${url.startsWith('https:')?' rel="noreferrer noopener"':''}>${escapeHtml(label)}</a>`:`<span>${escapeHtml(label)} (source reference unavailable)</span>`);});
  return escapeHtml(s).replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>').replace(/\u0000(\d+)\u0000/g,(_,n)=>tokens[Number(n)]);
 };
 let output='',paragraph=[],code=null,list=false;const flush=()=>{if(paragraph.length){output+=`<p>${inline(paragraph.join(' '))}</p>`;paragraph=[];}if(list){output+='</ul>';list=false;}};
 for(const line of doc.markdown.split(/\r?\n/)){
  if(line.startsWith('```')){flush();if(code!==null){output+=`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`;code=null;}else code=[];continue;}
  if(code!==null){code.push(line);continue;}
  const h=/^(#{1,6})\s+(.+)$/.exec(line);if(h){flush();const n=Math.min(6,h[1].length+1);output+=`<h${n} id="${escapeHtml(headingId(h[2]))}">${inline(h[2])}</h${n}>`;continue;}
  if(/^[-*] /.test(line)){if(paragraph.length){output+=`<p>${inline(paragraph.join(' '))}</p>`;paragraph=[];}if(!list){output+='<ul>';list=true;}output+=`<li>${inline(line.slice(2))}</li>`;continue;}
  if(!line.trim()){flush();continue;}paragraph.push(line);
 }
 flush();if(code!==null)output+=`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`;return output;
}

