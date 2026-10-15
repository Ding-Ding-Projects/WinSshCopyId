import {guidanceInventory,guidanceFamily,sampleFilter} from './guidance-data.mjs';
let sequence=0;
export function installGuidance(root,language){
 const translate=(en,yue)=>language()==='yue'?yue:language()==='both'?en+' / '+yue:en;
 const scan=()=>{
  for(const target of root.querySelectorAll('md-filled-button,md-outlined-button,md-text-button,md-outlined-text-field,md-slider,md-switch,md-checkbox,input[type=file],a,summary')){
   if(target.dataset.guidanceTarget||target.closest('[data-guidance-internal]')||!target.parentElement)continue;
   const label=target.label||target.getAttribute('aria-label')||target.textContent.trim();const family=guidanceFamily(label,target.tagName);target.dataset.guidanceTarget=family??'unsupported';
   if(!family){target.dataset.guidanceReason='No reviewed adapter exists for this control type; guidance coverage remains incomplete.';continue;}
   const record=guidanceInventory.find(x=>x.id===family),wrap=document.createElement('div');wrap.className='guided-control';wrap.dataset.guidanceFamily=family;
   const info=document.createElement('md-icon-button');info.dataset.guidanceInternal='true';info.className='info-entry';const name=translate('Help for '+label,'了解「'+label+'」');info.setAttribute('aria-label',name);info.title=name;info.setAttribute('aria-expanded','false');const icon=document.createElement('img');icon.src='assets/info.svg';icon.alt='';icon.width=20;icon.height=20;info.append(icon);
   const tip=document.createElement('span');tip.className='info-tooltip';tip.textContent=name;tip.setAttribute('role','tooltip');
   const panel=document.createElement('section');panel.dataset.guidanceInternal='true';panel.className='guidance-panel';panel.id='control-help-'+(++sequence);panel.hidden=true;info.setAttribute('aria-controls',panel.id);
   info.onclick=event=>{
    event.preventDefault();event.stopPropagation();
    const open=panel.hidden;panel.hidden=!open;info.setAttribute('aria-expanded',String(open));panel.replaceChildren();if(!open){target.focus();return;}
    const heading=document.createElement('h3');heading.textContent=label;const explanation=document.createElement('p');explanation.textContent=translate('For '+label+': '+record.en,'「'+label+'」：'+record.yue);const step=document.createElement('p');step.textContent=translate('Review this explanation, close the panel, then use the original control below. The direct control remains available.','先閱讀說明，關閉面板後可以直接使用下面嘅原本控制項。');
    const limitation=document.createElement('p');limitation.className='muted';limitation.textContent=translate('Guidance coverage: partial. This isolated example demonstrates local filtering only; a complete action-specific wizard and playground for this control remain unfinished.','指引覆蓋：部分完成。獨立示例只示範本機篩選；呢個控制項完整嘅專用精靈同練習仍未完成。');panel.append(heading,explanation,step,limitation);
    if(family==='search'){
     const input=document.createElement('md-outlined-text-field');input.label=translate('Example query','示例搜尋');input.value='Alpha';const result=document.createElement('p');result.setAttribute('role','status');const run=()=>{try{result.textContent=sampleFilter(input.value).join(', ')||translate('No matches.','冇符合條件嘅記錄。');}catch(e){result.textContent=e.message;}};const tryButton=document.createElement('md-outlined-button');tryButton.textContent=translate('Try example','試用示例');tryButton.onclick=run;tryButton.title=translate('Filter two synthetic articles without changing the real collection.','篩選兩篇模擬文章，唔會更改真實集合。');const reset=document.createElement('md-text-button');reset.textContent=translate('Reset example','重設示例');reset.onclick=()=>{input.value='Alpha';run();};reset.title=translate('Restore the initial synthetic query.','還原最初嘅模擬搜尋。');panel.append(input,tryButton,reset,result);run();
    }
   };
   const anchor=target.tagName==='SUMMARY'?target.parentElement:target;anchor.before(wrap);wrap.append(info,tip,panel,anchor);
  }
 };
 scan();const observer=new MutationObserver(scan);observer.observe(root,{childList:true,subtree:true});return ()=>observer.disconnect();
}

