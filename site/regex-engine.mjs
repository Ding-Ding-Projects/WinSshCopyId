export function evaluateRegex({pattern='',flags='',sample='',replacement='',mode='regex',items=[]}){
 if(pattern.length>4096||sample.length>262144||replacement.length>4096||items.length>1000||items.some(x=>typeof x!=='string'||x.length>262144))throw Error('Search input exceeds its bound.');
 if(!/^[gimsuy]*$/.test(flags)||new Set(flags).size!==flags.length)throw Error('Unsupported or duplicate flags.');
 const source=mode==='plain'?pattern.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'):pattern;
 // Enumeration has its own global cursor. Matching and replacement keep requested flags exact.
 const enumeration=new RegExp(source,flags.includes('g')?flags:flags+'g');
 const regex=new RegExp(source,flags);const start=performance.now();const matches=[];let m;
 while((m=enumeration.exec(sample))!==null&&matches.length<1000){matches.push({index:m.index,value:m[0],captures:m.slice(1),groups:m.groups??{}});if(m[0]==='')enumeration.lastIndex=m.index+(flags.includes('u')&&sample.codePointAt(m.index)>0xFFFF?2:1);}
 const limited=matches.length===1000;const replaced=limited?null:sample.replace(new RegExp(source,flags),replacement);if(replaced!==null&&replaced.length>262144)throw Error('Replacement output exceeds its bound.');
 const indices=[];for(let i=0;i<items.length;i++){regex.lastIndex=0;if(!pattern||regex.test(items[i]))indices.push(i);}
 return {matches,replaced,limited,indices,milliseconds:performance.now()-start,flags,enumeration:'All matches using a separate global cursor; requested sticky and Unicode semantics are preserved.',engine:'ECMAScript RegExp, installed browser engine'};
}
