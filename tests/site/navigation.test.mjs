import test from 'node:test';
import assert from 'node:assert/strict';
import {publicRouteTitle,bindSkipLink} from '../../site/core.mjs';
import {copy} from '../../site/locales.mjs';
test('public titles use shipped labels and exact source article titles',()=>{
 assert.equal(publicRouteTitle('settings','en',copy,[]),'Settings · WinSshCopyId');
 assert.equal(publicRouteTitle('settings','yue',copy,[]),'設定 · WinSshCopyId');
 assert.equal(publicRouteTitle('settings','both',copy,[]),'Settings / 設定 · WinSshCopyId');
 assert.equal(publicRouteTitle('article/docs/a','en',copy,[{id:'docs/a',title:'Original article'}]),'Original article · WinSshCopyId');
 assert.equal(publicRouteTitle('untrusted-route','en',copy,[]),'Documentation · WinSshCopyId');
});
test('skip activation cancels fragment navigation and focuses the existing main',()=>{
 let listener,prevented=false,focused=0,scrolled=0;
 const link={addEventListener:(name,fn)=>{assert.equal(name,'click');listener=fn;}};
 const main={focus:()=>focused++,scrollIntoView:()=>scrolled++};
 bindSkipLink(link,main);listener({preventDefault:()=>prevented=true});
 assert.equal(prevented,true);assert.equal(focused,1);assert.equal(scrolled,1);
});
