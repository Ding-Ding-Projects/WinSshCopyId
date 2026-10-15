import test from 'node:test';
import assert from 'node:assert/strict';
import {required,inspectCoverage} from './coverage.mjs';
test('coverage rejects every individually removed row and evidence boundary',()=>{
 const fixture={rows:required.map(id=>({id,status:'verified',implementation:'fixture',article:'fixture',localization:'fixture',persistence:'fixture',tests:'fixture',interaction:'fixture',capture:'fixture'}))};
 assert.deepEqual(inspectCoverage(fixture),[]);
 for(const id of required){assert.ok(inspectCoverage({rows:fixture.rows.filter(r=>r.id!==id)}).length);for(const key of ['implementation','article','localization','persistence','tests','interaction','capture']){const copy=structuredClone(fixture);delete copy.rows.find(r=>r.id===id)[key];assert.ok(inspectCoverage(copy).length);}}
 assert.deepEqual(inspectCoverage(fixture),[]);
});
