export function validatePreferences(value){
 if(!value||typeof value!=='object'||Array.isArray(value)||value.schemaVersion!==1)throw Error('Unsupported settings schema.');
 const allowed=['schemaVersion','language','theme','englishHumour','cantoneseHumour','emoji','density','fontSize','accent','dock','tabs','pins','groups'];
 if(Object.keys(value).some(k=>!allowed.includes(k)))throw Error('Unknown preference field.');
 if(!['en','yue','both'].includes(value.language)||!['light','dark'].includes(value.theme)||!['comfortable','compact'].includes(value.density)||!['left','right','top','bottom'].includes(value.dock)||typeof value.emoji!=='boolean'||!/^#[a-f\d]{6}$/i.test(value.accent))throw Error('Invalid preference.');
 for(const k of ['englishHumour','cantoneseHumour'])if(!Number.isInteger(value[k])||value[k]<1||value[k]>5)throw Error('Invalid humour level.');
 if(!Number.isInteger(value.fontSize)||value.fontSize<14||value.fontSize>24)throw Error('Invalid text size.');
 for(const k of ['tabs','pins'])if(!Array.isArray(value[k])||value[k].length>100||new Set(value[k]).size!==value[k].length||value[k].some(x=>typeof x!=='string'||x.length>250))throw Error('Invalid tab state.');
 if(!value.groups||Array.isArray(value.groups)||typeof value.groups!=='object'||Object.keys(value.groups).length>100||Object.entries(value.groups).some(([k,v])=>k.length>250||typeof v!=='string'||v.length>80||['__proto__','constructor','prototype'].includes(k)))throw Error('Invalid tab groups.');
 return value;
}
export function annotatePattern(pattern){
 const tokens=[];const matcher=/\\(?:p\{[^}]*\}|u\{[^}]*\}|u[0-9a-fA-F]{4}|.)|\(\?(?:<[=!]|[:=!]|<[^>]+>)|\[[^\]]*\]|\{\d+(?:,\d*)?\}[?+]?|[()*+?|^$.]|[^\\[()*+?|^$.]+/g;let m;
 while((m=matcher.exec(pattern)))tokens.push({offset:m.index,text:m[0],kind:m[0][0]==='\\'?'escape or backreference':m[0][0]==='['?'character class':m[0][0]==='('?'group or assertion':'pattern token'});
 return {tokens,note:'Lexical annotation, not an engine parse tree. Runtime compilation is authoritative. Nested quantifiers can cause expensive backtracking; worker deadlines remain enforced.'};
}
