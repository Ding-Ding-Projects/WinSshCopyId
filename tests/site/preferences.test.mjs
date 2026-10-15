import test from 'node:test';
import assert from 'node:assert/strict';
import {validatePreferences,annotatePattern} from '../../site/preferences.mjs';
const sample={schemaVersion:1,language:'en',theme:'light',englishHumour:5,cantoneseHumour:5,emoji:true,density:'comfortable',fontSize:16,accent:'#285ea7',dock:'left',tabs:['home'],pins:['home'],groups:{}};
test('preferences reject unknown private payloads and invalid persisted settings',()=>{assert.equal(validatePreferences(sample),sample);for(const patch of [{language:'other'},{fontSize:100},{englishHumour:0},{tabs:['home','home']},{personalWording:{x:'y'}},{accent:'red'}])assert.throws(()=>validatePreferences({...sample,...patch}));});
test('regex annotations identify lexical boundaries without claiming an engine parse tree',()=>{const r=annotatePattern('(?<name>\\p{L}+)');assert.ok(r.tokens.some(x=>x.text==='\\p{L}'));assert.match(r.note,/not an engine parse tree/);});
