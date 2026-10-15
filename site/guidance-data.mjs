// Hand-written guidance families cover each current control role. Runtime instances retain exact target labels.
export const guidanceInventory=[
 ['navigation','Open a documentation destination without running the installed application.','開啟文件目的地，唔會執行已安裝嘅應用程式。'],
 ['tab-options','Change this tab position, pin or group. Closing a pinned or home tab is refused.','更改呢個分頁嘅位置、釘選或群組。已釘選同首頁分頁唔會關閉。'],
 ['search','Filter only this collection. Plain text is the default; the adjacent builder explicitly enables regular expressions.','只篩選呢個集合，預設使用純文字；旁邊工具可以明確啟用正則運算式。'],
 ['regex','Construct and test this search pattern in a bounded worker. Invalid syntax or a timeout leaves the action incomplete.','喺有限時嘅工作程序建立同測試呢個搜尋模式。語法無效或逾時會清楚顯示未完成。'],
 ['selection','Select records for the stated collection action. Select all means current matches, not unrelated records.','選取記錄以進行指定操作。全選只包括目前符合條件嘅記錄。'],
 ['export','Create the selected local export. Review its scope first; private wording data and file metadata are excluded.','建立所選本機匯出。先核對範圍；私人用語資料同檔案中繼資料唔會包括在內。'],
 ['language','Choose the website language. Article bodies keep their source language until translations are delivered.','選擇網站語言。文章喺翻譯完成前保留原文。'],
 ['appearance','Change only this visitor preference. The installed application and stable product identity are unchanged.','只更改目前訪客設定，唔會更改已安裝應用程式同產品身份。'],
 ['humour','Set this language independently from 1 (serious) to 5 (playful). Both defaults are 5; facts remain unchanged.','獨立設定呢種語言，由 1（認真）至 5（活潑）。兩者預設都係 5，事實唔會改變。'],
 ['wording','Choose a local schema-version-1 JSON file. It is validated before private display use; clear restores original wording.','選取本機版本 1 JSON 檔案，驗證後先用於私人顯示；清除會還原原本用語。'],
 ['settings-import','Import validated website preferences. Invalid data leaves the current preferences unchanged.','匯入經驗證嘅網站設定。資料無效時保留目前設定。'],
 ['reset','This consequential action uses acknowledgement plus the exact confirmation phrase. The example never resets real state.','呢項重要操作需要確認同輸入指定字句。示例絕不會重設真實狀態。'],
 ['history','Restore the selected preference snapshot as a new local history entry. Private wording is never recovered from history.','將所選設定快照還原成新嘅本機歷史記錄。歷史唔會還原私人用語。'],
 ['dialog','Close this optional panel and return focus to its originating control.','關閉呢個自選面板，焦點會返回原本控制項。'],
 ['skip','Move keyboard focus to the current reading area without changing the selected route.','將鍵盤焦點移至目前閱讀區域，唔會更改路線。'],
 ['disclosure','Expand or collapse this local section. Hidden content stays available by reopening it.','展開或收起呢個部分，可以隨時重新開啟。']
].map(([id,en,yue])=>({id,en,yue,status:'partial',reason:'Contextual explanation is implemented. The synthetic filtering example is interactive; complete action-specific multistep adapters and localized advanced workflows remain incomplete.'}));
export function guidanceFamily(label,tag){
 if(tag==='A')return label==='Skip to content'?'skip':'navigation';
 if(tag==='SUMMARY')return 'disclosure';
 if(tag==='MD-CHECKBOX')return /acknowledge/i.test(label)?'reset':'selection';
 if(/import settings/i.test(label))return 'settings-import';
 if(/JSON file|local wording|本機用語|JSON 檔案/.test(label))return 'wording';
 if(/RESET|reset|irreversible|consequence/.test(label))return 'reset';
 if(/Restore this/.test(label))return 'history';
 if(/Export|Print/.test(label))return 'export';
 if(/Select all|Invert/.test(label))return 'selection';
 if(/humour|幽默/.test(label))return 'humour';
 if(/Search this|搜尋目前/.test(label))return 'search';
 if(/Pattern|Flags:|Sample text|Replacement|Regex builder|正則運算式|Plain text|Regular expression|Test pattern|Cancel evaluation|Digits|Unicode letters|Anchor|Capture|Lookahead|Lookbehind|Alternative|Lazy|Backreference/.test(label))return 'regex';
 if(/Tab options|Pin \/ unpin|Move earlier|Move later|Set group|Close tab|^Group$|^left$|^right$|^top$|^bottom$/.test(label))return 'tab-options';
 if(/English|廣東話/.test(label))return 'language';
 if(/Light|Dark|Comfortable|Compact|Text size|Decorative emoji|Accent color/.test(label)||tag==='MD-SLIDER'||tag==='MD-SWITCH')return 'appearance';
 if(/^Close$|^關閉$|Close \/ 關閉/.test(label))return 'dialog';
 if(tag.includes('BUTTON'))return 'navigation';
 return null;
}
export function inspectGuidance(rows){const missing=[];for(const expected of guidanceInventory){if(!rows.some(r=>r.id===expected.id))missing.push(expected.id);}for(const r of rows){if(!r.en||!r.yue||!r.reason)missing.push(r.id+': details');}return missing;}
export function sampleFilter(query){if(typeof query!=='string'||query.length>160)throw Error('Use at most 160 characters.');return ['Alpha article','Beta article'].filter(x=>x.toLowerCase().includes(query.toLowerCase()));}
