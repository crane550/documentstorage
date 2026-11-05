import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir,readFile,writeFile,access } from 'node:fs/promises';
import path from 'node:path';
import { credentials,signIn,connectStorage,openFolder,useAsInbox,settleAndRescan,inbox,openInboxItem,toast,ready,noOverflow,chromiumPath } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5246';assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname));
const directory=path.resolve('.local/organizing-browser-'+Date.now());
for(const folder of ['Incoming','Cedar Churhc/Safety Minstry','Finance'])await mkdir(path.join(directory,folder),{recursive:true});
const ministry='Synthetic West campus ministry post guide. Revision five.';
for(const [name,text] of [['ministry.txt',ministry],['credit.txt','Synthetic home equity credit line modification.'],['training.txt','Synthetic volunteer training notes.']])await writeFile(path.join(directory,'Incoming',name),text);
let calls=0,learned=false,wireError,active=0,maxActive=0;
const fixture=http.createServer(async(req,res)=>{
 try{
  res.setHeader('Content-Type','application/json');if(req.url==='/api/tags')return res.end(JSON.stringify({models:[{name:'synthetic-organizer:latest',size:123}]}));
  let raw='';for await(const chunk of req)raw+=chunk;const payload=JSON.parse(raw);
  if(req.url==='/api/show')return res.end('{"capabilities":["completion"]}');
  if(req.url==='/api/chat'){
   calls++;active++;maxActive=Math.max(maxActive,active);
   const input=JSON.parse(payload.messages[1].content),catalog=input.workspace;
   assert(payload.format.properties.filing);assert(!payload.format.properties.document_type.enum);assert(!JSON.stringify(catalog).includes(directory));
   const credit=input.file_name==='credit.txt',training=input.file_name==='training.txt';
   const target=catalog.folders.find(x=>x.path.endsWith(credit?'Finance':'Safety Minstry'));assert(target,'existing destination is supplied');
   learned ||= catalog.approved_examples.some(x=>x.approved_name==='Approved West campus guide.txt');
   const proposal={document_type:credit?'heloc_modification':training?'volunteer_training':'ministry_post_guide',needs_review:false,summary:'Synthetic organizing suggestion.',facts:credit||training?[]:[{name:'campus',value:'West',evidence:'Synthetic West campus'}],
    filing:{destination_folder_id:target.id,new_folder:training?'Training':'',filename:credit?'Credit line modification.txt':training?'Volunteer training.txt':'West campus guide - Rev 5.txt',reason:'The existing folder matches the document subject. A filing rule is not required.'}};
   active--;return res.end(JSON.stringify({done:true,message:{content:JSON.stringify(proposal)}}));
  }
  res.statusCode=404;res.end('{}');
 }catch(e){wireError=e;active=Math.max(0,active-1);res.statusCode=500;res.end('{}');}
});
await new Promise(resolve=>fixture.listen(0,'127.0.0.1',resolve));const endpoint='http://127.0.0.1:'+fixture.address().port;
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});const page=await browser.newPage({viewport:{width:1600,height:1100}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0,enabled=false;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
async function read(name){await openInboxItem(page,name);await page.getByRole('button',{name:'Read with AI',exact:false}).first().click();await page.locator('.organizing-proposal').waitFor({timeout:25000});}
async function reader(){await page.goto(base+'/settings?tab=reader');await ready(page);await page.getByLabel('Ollama server address',{exact:true}).waitFor();}
try{
 await page.goto(base);await signIn(page,await credentials());await connectStorage(page,base,'Organizing test archive',directory);await openFolder(page,'Incoming');await useAsInbox(page);await settleAndRescan(page);
 await reader();await page.getByLabel('Ollama server address',{exact:true}).fill(endpoint);await page.getByRole('button',{name:'Check connection & list models',exact:true}).click();await toast(page,'Connected. 1 local model');await page.getByLabel('Installed model',{exact:true}).fill('synthetic-organizer:latest');await page.getByLabel('Use the AI reader',{exact:true}).check();await page.getByRole('button',{name:'Save reader settings',exact:true}).click();await toast(page,'AI reader on');enabled=true;
 await inbox(page);await read('ministry.txt');
 await check(page.getByLabel('Suggested filename',{exact:true}).inputValue().then(x=>x==='West campus guide - Rev 5.txt'),'an unfamiliar ministry document receives a complete proposed filename without configuring its type');
 await check(page.getByLabel('Destination folder',{exact:true}).locator('option:checked').textContent().then(x=>x.includes('Cedar Churhc')&&x.includes('Safety Minstry')),'the AI selects the existing ministry folder by ID despite spelling errors');
 await check(readFile(path.join(directory,'Incoming/ministry.txt'),'utf8').then(x=>x===ministry),'reading and proposing do not move the original');
 await check(page.getByText('Text ready for AI reading',{exact:true}).isVisible(),'the filing slip explicitly reports server text readiness');
 await page.locator('.reading-details > summary').click();
 await check(page.locator('.reading-details').textContent().then(t=>t.includes('synthetic-organizer:latest')&&t.includes(ministry)&&t.includes('Safety Minstry')),'reading details show the actual model, submitted excerpt and destination context');
 await page.locator('.extraction-status').getByRole('button',{name:'Rescan text',exact:true}).click();await toast(page,'Text rescanned:');
 await page.locator('.reader-note').filter({hasText:'Text was rescanned'}).waitFor();
 await check(page.locator('.organizing-proposal').count().then(n=>n===0),'rescan removes the previous executable AI proposal');
 await page.locator('.reader-note').getByRole('button',{name:'Try again',exact:true}).click();await page.locator('.organizing-proposal').waitFor({timeout:25000});

 await page.getByLabel('Suggested filename',{exact:true}).fill('Approved West campus guide.txt');await page.getByLabel('Filing preference or correction (optional)',{exact:true}).fill('Keep all campus ministry guides together in this existing folder.');
 await page.screenshot({path:'.local/organizing-proposal.png',fullPage:true});await page.getByRole('button',{name:'Approve & file',exact:true}).click();await toast(page,'Filed as Approved West campus guide.txt');
 await check(readFile(path.join(directory,'Cedar Churhc/Safety Minstry/Approved West campus guide.txt'),'utf8').then(x=>x===ministry),'one approval files unchanged bytes using the corrected filename and existing folder');
 await check(access(path.join(directory,'Incoming/ministry.txt')).then(()=>false,()=>true),'approved filing removes the source from intake through the move journal');
 await read('credit.txt');await check(learned,'a later AI request receives the previously approved correction as an example');
 await page.getByRole('button',{name:'Decline suggestion',exact:true}).click();await page.locator('.reader-note').filter({hasText:'declined'}).waitFor();await check(access(path.join(directory,'Incoming/credit.txt')).then(()=>true,()=>false),'declining a plan leaves its original in intake');
 await read('training.txt');await check(page.getByLabel('Create a new subfolder here',{exact:true}).isChecked(),'the proposal explicitly shows when approval will create a new subfolder');
 await page.getByRole('button',{name:'Approve & file',exact:true}).click();await toast(page,'Filed as Volunteer training.txt');await check(readFile(path.join(directory,'Cedar Churhc/Safety Minstry/Training/Volunteer training.txt'),'utf8').then(x=>x==='Synthetic volunteer training notes.'),'approval creates the proposed subfolder and files without a naming rule');
 await reader();await page.getByText(/Remembered filing examples/).click();await check(page.locator('.filing-example').count().then(x=>x===2),'remembered examples are visible in reader settings');await page.locator('.filing-example').filter({hasText:'Approved West campus guide.txt'}).getByRole('button',{name:'Forget example',exact:true}).click();await toast(page,'Example forgotten');await check(page.locator('.filing-example').count().then(x=>x===1),'users can remove a learned example without moving its document');
 await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'remembered examples and reader settings fit mobile screens');await page.getByRole('button',{name:'Open menu',exact:true}).click();await inbox(page);await openInboxItem(page,'credit.txt');await page.locator('.reader-note').getByRole('button',{name:'Try again',exact:true}).click();await page.locator('.organizing-proposal').waitFor({timeout:25000});await check(noOverflow(page),'the full AI filing proposal fits mobile screens');
 await check(maxActive===1&&calls>=4&&!wireError,'real queued requests use the organizing protocol one document at a time');await check(errors.length===0,'organizing approvals cause no browser script errors');console.log(`All ${checks} organizing browser checks passed (protocol fixture, not model accuracy).`);
}finally{if(enabled){await reader();await page.getByLabel('Use the AI reader',{exact:true}).uncheck();await page.getByRole('button',{name:'Save reader settings',exact:true}).click();await toast(page,'AI reader off');}await browser.close();await new Promise(resolve=>fixture.close(resolve));}
