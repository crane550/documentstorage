import { chromium } from 'playwright';
import http from 'node:http';
import assert from 'node:assert/strict';
import path from 'node:path';
import {mkdir,readFile,writeFile,access} from 'node:fs/promises';
import {credentials,chromiumPath,signIn,ready,connectStorage,toast,noOverflow,openFolder} from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5250';assert.equal(new URL(base).hostname,'127.0.0.1');
const directory=path.resolve('.local/desk-browser-'+Date.now());await mkdir(path.join(directory,'Finance'),{recursive:true});await mkdir(path.join(directory,'Cedar Churhc/Safety Ministry'),{recursive:true});
await writeFile(path.join(directory,'Finance/equity.txt'),'Synthetic home equity agreement. The interest rate is 5.25 percent. Only synthetic information is in this fixture.');
const pdfFixture=JSON.parse(await readFile('.local/preview-fixture.json','utf8'));await writeFile(path.join(directory,'Finance/statement.pdf'),await readFile(path.join(pdfFixture.directory,'statement.pdf')));
let equityId,historySeen=false,planHistory=false,calls=0,wireError,shows=0,maxActive=0,active=0;
const fixture=http.createServer(async(req,res)=>{
 try{
  res.setHeader('Content-Type','application/json');if(req.url==='/api/tags')return res.end(JSON.stringify({models:[{name:'synthetic-desk:latest',size:123}]}));
  let body='';for await(const chunk of req)body+=chunk;const input=JSON.parse(body);
  if(req.url==='/api/show'){shows++;assert(!body.includes('5.25'));return res.end('{"capabilities":["completion"]}');}
  if(req.url==='/api/chat'){
   active++;maxActive=Math.max(maxActive,active);calls++;assert(shows>=calls);const request=JSON.parse(input.messages[1].content);assert(!JSON.stringify(request).includes(directory));
   let answer;
   if(request.mode==='ask'){
    historySeen ||= request.history.some(x=>x.question.includes('interest rate')&&x.reply.includes('5.25'));
    const source=request.workspace.sources.find(x=>request.question.includes('page')?x.Name==='statement.pdf'&&x.Page===2:x.Name==='equity.txt');assert(source,'the correct indexed source passage was retrieved');
    if(source.Name==='equity.txt')equityId=source.EntryId;
    const quote=source.Name==='equity.txt'?'The interest rate is 5.25 percent.':'Synthetic statement page 2';
    answer={reply:`${source.Name==='equity.txt'?'The agreement lists 5.25 percent.':'This is the second statement page.'} [${source.Id}] <script>window.deskPwned=true</script>`,insufficient:false,citations:[{source_id:source.Id,quote}]};
   }else{
    assert.equal(request.mode,'folders');const folders=request.workspace.folders;const scope=folders.find(x=>x.id===request.workspace.scope_id);const safety=folders.find(x=>x.name==='Safety Ministry');assert(scope&&safety);
    planHistory ||= request.history.some(x=>x.plan.length===3);
    answer={reply:'Reuse the existing safety ministry folder and add nested training folders.',insufficient:false,folders:[{key:'F1',parent:scope.id,name:safety.name,instructions:'Safety ministry guides and post positions belong here.',existing_id:safety.id},{key:'F2',parent:'F1',name:'Training',instructions:'Volunteer training and refresher documents belong here.',existing_id:''},{key:'F3',parent:'F2',name:request.question.includes('Annual refreshers')?'Annual refreshers':'Yearly sessions',instructions:'Annual volunteer refresher documents belong here.',existing_id:''}]};
   }
   active--;return res.end(JSON.stringify({done:true,message:{content:JSON.stringify(answer)}}));
  }
  res.statusCode=404;res.end('{}');
 }catch(error){wireError=error;active=Math.max(0,active-1);res.statusCode=500;res.end('{}');}
});
await new Promise(resolve=>fixture.listen(0,'127.0.0.1',resolve));const endpoint='http://127.0.0.1:'+fixture.address().port;
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});const page=await browser.newPage({viewport:{width:1700,height:1100}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let enabled=false,count=0;
async function check(value,label){assert(await value,label);count++;console.log('PASS '+label);}
async function reader(){await page.goto(base+'/settings?tab=reader');await ready(page);await page.getByLabel('Ollama server address',{exact:true}).waitFor();}
async function send(text){const before=await page.locator('.desk-turn').count();await page.getByLabel('Ask your documents',{exact:true}).fill(text);await page.getByRole('button',{name:'Send',exact:true}).click();await page.waitForFunction(n=>document.querySelectorAll('.desk-turn').length>n,before);await page.waitForFunction(()=>document.querySelector('.desk-turn:last-child .desk-answer'));}
async function plan(text){const before=await page.locator('.desk-turn').count();await page.getByLabel('Describe or refine the folder plan',{exact:true}).fill(text);await page.getByRole('button',{name:'Send',exact:true}).click();await page.waitForFunction(n=>document.querySelectorAll('.desk-turn').length>n,before);await page.waitForFunction(()=>document.querySelector('.desk-turn:last-child .plan-node'));
}
try{
 await page.goto(base);await signIn(page,await credentials());await connectStorage(page,base,'Desk test archive',directory);await reader();await page.getByLabel('Ollama server address',{exact:true}).fill(endpoint);await page.getByRole('button',{name:'Check connection & list models',exact:true}).click();await toast(page,'Connected. 1 local model');await page.getByLabel('Installed model',{exact:true}).fill('synthetic-desk:latest');await page.getByLabel('Use the AI reader',{exact:true}).check();await page.getByRole('button',{name:'Save reader settings',exact:true}).click();await toast(page,'AI reader on');enabled=true;
 await page.goto(base+'/ask');await ready(page);await send('What interest rate is on the home equity agreement?');
 await check(page.locator('.desk-answer').last().textContent().then(x=>x.includes('5.25 percent')),'a natural-language question returns a cited answer from actual indexed text');await check(page.evaluate(()=>window.deskPwned===undefined),'answer text cannot execute document or model markup');
 await page.locator('.desk-citation').first().getByRole('button').click();await page.locator('.desk-source .source-preview').filter({hasText:'5.25 percent'}).waitFor();await check(true,'a citation opens the original document in a side-by-side application preview');
 await send('Is that the interest rate?');await check(historySeen,'follow-up questions receive saved earlier questions and answers');
 await page.reload();await ready(page);await check(page.locator('.desk-turn').count().then(x=>x===2),'conversation history survives page reload');
 await send('Show evidence from page 2 of the Cedar Bank statement.');await page.locator('.desk-turn').last().locator('.desk-citation').getByRole('button').click();await page.waitForFunction(()=>document.querySelector('.desk-source .document-preview')?.dataset.loaded==='true');await check(page.locator('.desk-source').getByLabel('PDF page',{exact:true}).inputValue().then(x=>x==='2'),'PDF citations open the cited page inside the app');
 await page.screenshot({path:'.local/desk-answer.png',fullPage:true});await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'conversations and cited previews fit a mobile viewport');await page.setViewportSize({width:1700,height:1100});
 await page.goto(base+'/folder-planner');await ready(page);await page.getByLabel('Plan inside',{exact:true}).selectOption({label:'Desk test archive / Cedar Churhc'});
 await plan('Add training and yearly sessions beneath the existing safety ministry folder.');await check(access(path.join(directory,'Cedar Churhc/Safety Ministry/Training')).then(()=>false,()=>true),'a conversational folder draft makes no filesystem changes');
 await plan('Call yearly sessions Annual refreshers.');await check(planHistory&&await page.getByLabel('Folder name',{exact:true}).nth(2).inputValue()==='Annual refreshers','the reader receives the preceding plan and can refine the complete nested proposal');
 await page.getByLabel('Folder name',{exact:true}).nth(1).fill('Team Training');await page.getByLabel('Filing instructions',{exact:true}).nth(1).fill('Keep training records together, regardless of year.');await page.getByRole('button',{name:'Review exact folder changes',exact:true}).click();await page.getByRole('heading',{name:'Exact changes to approve',exact:true}).waitFor();
 await check(page.locator('.plan-confirm').textContent().then(x=>x.includes('Cedar Churhc/Safety Ministry/Team Training/Annual refreshers')),'the review shows exact nested paths after manual edits');
 await page.getByRole('button',{name:'Create folders & save instructions',exact:true}).click();await page.getByText('Folders and instructions saved.',{exact:true}).waitFor();
 await check(access(path.join(directory,'Cedar Churhc/Safety Ministry/Team Training/Annual refreshers')).then(()=>true,()=>false),'approval creates the actual nested folder tree');await check(readFile(path.join(directory,'Finance/equity.txt'),'utf8').then(x=>x.includes('5.25 percent')),'folder planning preserves all existing document bytes');
 await page.screenshot({path:'.local/desk-plan.png',fullPage:true});await page.getByRole('link',{name:'Cedar Churhc/Safety Ministry/Team Training',exact:true}).click();await check(page.locator('.instruction-canonical').textContent().then(x=>x.includes('Keep training records together')),'approved instructions are available immediately on the real folder');
 await page.goto(base+'/folder-planner');await ready(page);await page.locator('.desk-thread').first().click();await page.getByText('Folders and instructions saved.',{exact:true}).waitFor();await check(true,'applied folder plans and step receipts survive navigating away and returning');
 await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'nested folder plans and progress fit mobile screens');await check(maxActive===1&&!wireError&&errors.length===0,'document questions and folder planning use the local protocol without concurrent inference or browser script errors');await page.goto(base+'/library?doc='+equityId);await ready(page);await page.getByRole('link',{name:'Ask about this document',exact:true}).waitFor();await check(noOverflow(page),'the Library document actions fit mobile screens with the new Ask control');await page.getByRole('link',{name:'Ask about this document',exact:true}).click();await page.getByLabel('Search within',{exact:true}).locator('option:checked').filter({hasText:'equity.txt (one document)'}).waitFor({state:'attached'});await check(true,'Ask about this document opens a conversation scoped to that original file');console.log(`All ${count} conversational browser checks passed (protocol fixture, not model quality).`);
}finally{if(enabled){await reader();await page.getByLabel('Use the AI reader',{exact:true}).uncheck();await page.getByRole('button',{name:'Save reader settings',exact:true}).click();await toast(page,'AI reader off');}await browser.close();await new Promise(resolve=>fixture.close(resolve));}
