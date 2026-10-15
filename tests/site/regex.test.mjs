import test from 'node:test';
import assert from 'node:assert/strict';
import {evaluateRegex} from '../../site/regex-engine.mjs';
test('actual regex supports captures, Unicode, replacement, multiline and zero-width bounds',()=>{
 let r=evaluateRegex({pattern:'(?<word>\\p{L}+)',flags:'u',sample:'hello 世界',replacement:'[$<word>]'});assert.equal(r.matches.length,2);assert.equal(r.replaced,'[hello] [世界]');
 assert.equal(evaluateRegex({pattern:'^b',flags:'m',sample:'a\nb'}).matches.length,1);
 assert.equal(evaluateRegex({pattern:'(?=.)',sample:'😀a'}).matches.length,2);
 assert.equal(evaluateRegex({pattern:'.',mode:'plain',sample:'a.b'}).matches.length,1);
 assert.throws(()=>evaluateRegex({pattern:'['}));assert.throws(()=>evaluateRegex({flags:'ii'}));assert.throws(()=>evaluateRegex({sample:'x'.repeat(262145)}));
 assert.equal(evaluateRegex({pattern:'x',items:['x','y']}).indices[0],0);
});
