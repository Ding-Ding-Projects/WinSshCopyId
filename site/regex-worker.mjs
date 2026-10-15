import {evaluateRegex} from './regex-engine.mjs';
onmessage=event=>{try{postMessage({ok:true,result:evaluateRegex(event.data)});}catch{postMessage({ok:false,error:'The pattern or input is invalid for this engine.'});}};
