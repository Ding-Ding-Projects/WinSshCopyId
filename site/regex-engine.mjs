export function evaluateRegex({pattern='',flags='',sample='',replacement='',mode='regex',items=[]}){
 if(pattern.length>4096||sample.length>262144||replacement.length>4096||items.length>1000||items.some(x=>typeof x!=='string'||x.length>262144))throw Error('Search input exceeds its bound.');
 if(!/^[gimsuy]*$/.test(flags)||new Set(flags).size!==flags.length)throw Error('Unsupported or duplicate flags.');
 const source=mode==='plain'?pattern.replace(/[.*+?^${}()|[\]\\]/g,'\\$&'):pattern;
 const engineFlags=[...new Set(flags.replaceAll('y','').split('').concat('g','u'))].join('');
 const regex=new RegExp(source,engineFlags);const start=performance.now();const matches=[];let m;
 while((m=regex.exec(sample))!==null&&matches.length<1000){matches.push({index:m.index,value:m[0],captures:m.slice(1),groups:m.groups??{}});if(m[0]==='')regex.lastIndex=m.index+(sample.codePointAt(m.index)>0xFFFF?2:1);}
 const limited=matches.length===1000;const replaced=limited?null:sample.replace(new RegExp(source,engineFlags),replacement);if(replaced!==null&&replaced.length>262144)throw Error('Replacement output exceeds its bound.');
 const indices=[];for(let i=0;i<items.length;i++){regex.lastIndex=0;if(!pattern||regex.test(items[i]))indices.push(i);}
 return {matches,replaced,limited,indices,milliseconds:performance.now()-start,engine:'ECMAScript RegExp, installed browser engine'};
}
