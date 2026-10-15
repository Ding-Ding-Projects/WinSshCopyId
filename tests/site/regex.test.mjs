import test from 'node:test';
import assert from 'node:assert/strict';
import {evaluateRegex} from '../../site/regex-engine.mjs';
test('actual regex supports captures, Unicode, replacement, multiline and zero-width bounds',()=>{
 let r=evaluateRegex({pattern:'(?<word>\\p{L}+)',flags:'gu',sample:'hello 世界',replacement:'[$<word>]'});assert.equal(r.matches.length,2);assert.equal(r.replaced,'[hello] [世界]');
 assert.equal(evaluateRegex({pattern:'^b',flags:'m',sample:'a\nb'}).matches.length,1);
 assert.equal(evaluateRegex({pattern:'(?=.)',flags:'u',sample:'😀a'}).matches.length,2);
 assert.equal(evaluateRegex({pattern:'.',mode:'plain',sample:'a.b'}).matches.length,1);
 assert.throws(()=>evaluateRegex({pattern:'['}));assert.throws(()=>evaluateRegex({flags:'ii'}));assert.throws(()=>evaluateRegex({sample:'x'.repeat(262145)}));
 assert.equal(evaluateRegex({pattern:'x',items:['x','y']}).indices[0],0);
});

test('replacement and item matching preserve requested global, sticky and Unicode flags',()=>{
 for(const value of [
  {pattern:'a',flags:'',sample:'aa',replacement:'x'},
  {pattern:'a',flags:'g',sample:'aa',replacement:'x'},
  {pattern:'a',flags:'y',sample:'ba',replacement:'x'},
  {pattern:'a',flags:'gy',sample:'aaba',replacement:'x'},
  {pattern:'\\u{61}',flags:'',sample:'a',replacement:'x'},
  {pattern:'\\u{61}',flags:'u',sample:'a',replacement:'x'},
  {pattern:'(?=.)',flags:'g',sample:'😀a',replacement:'x'},
  {pattern:'(?=.)',flags:'gu',sample:'😀a',replacement:'x'}
 ]){
  // Use the actual native engine as the semantic oracle.
  const input={...value,pattern:value.pattern.replaceAll('\\\\','\\')};
  const result=evaluateRegex({...input,items:['ba','aa']});
  assert.equal(result.replaced,input.sample.replace(new RegExp(input.pattern,input.flags),input.replacement));
  assert.deepEqual(result.indices,['ba','aa'].flatMap((s,i)=>new RegExp(input.pattern,input.flags).test(s)?[i]:[]));
 }
 assert.equal(evaluateRegex({pattern:'a',flags:'',sample:'aa',replacement:'x'}).matches.length,2);
 assert.equal(evaluateRegex({pattern:'(?=.)',sample:'😀a'}).matches.length,3);
});
