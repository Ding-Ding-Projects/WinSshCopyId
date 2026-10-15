import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
export const required=['info-icon-guidance','language','humour-and-emoji','personal-wording','shared-presentation-mode','narration-and-voices','scheduled-settings','display-name','attention-modes','dim-sum-catalogue','material-components','workflow-navigation','motion','appearance-editors','colour-picker','logo-customization','overlay-panels','collapsible-filters','accessibility-and-sizing','functional-controls','tabs-and-groups','regex-workbench','command-palette','context-menus','menu-shortcuts','rich-controls','guided-forms','settings-explanations','editor-presets','destructive-confirmation','element-locks','support-tickets','unlock-recovery','authenticator','qr-pairing','honest-monetization','local-history','exports','bulk-actions','changelog','complete-documentation','rendered-markdown','notifications','file-conversion-equivalent','server-management-equivalent','model-management-equivalent','external-editor-handoff','download-handoff','publishing-equivalent','operation-recovery','coverage-enforcement','front-provenance','product-captures','screen-recording','design-parity','design-record','responsive-matrix','public-capture-gallery','product-boundary','touch-layout','verified-installer-link','feature-articles','local-assets','social-preview','repository-homepage','private-wording-boundary','status-integration','wiki-content'];
export function inspectCoverage(ledger){
 const gaps=[];const rows=ledger?.rows??[];
 for(const id of required){const matches=rows.filter(r=>r.id===id);if(matches.length!==1){gaps.push(`${id}: expected one row`);continue;}const row=matches[0];if(row.status!=='verified')gaps.push(`${id}: ${row.status}`);for(const key of ['implementation','article','localization','persistence','tests','interaction','capture'])if(typeof row[key]!=='string'||!row[key].trim())gaps.push(`${id}: missing ${key}`);}
 for(const row of rows)if(!required.includes(row.id))gaps.push(`${row.id}: unknown row`);
 return gaps;
}
if(process.argv[1]&&path.resolve(process.argv[1])===fileURLToPath(import.meta.url)){
 const ledger=JSON.parse(fs.readFileSync(new URL('../../site/feature-ledger.json',import.meta.url),'utf8').replace(/^\uFEFF/,''));const gaps=inspectCoverage(ledger);console.log(`Coverage acceptance: ${gaps.length?'INCOMPLETE':'PASS'}; ${required.length} required rows; ${gaps.length} gaps.`);for(const gap of gaps)console.log(gap);process.exitCode=gaps.length?1:0;
}

