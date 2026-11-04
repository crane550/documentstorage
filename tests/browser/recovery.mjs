import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { readFile,writeFile,rename,stat,utimes,access } from 'node:fs/promises';
import path from 'node:path';
import { signIn, ready, openFolder, newFolder } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5243';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable loopback instance only');
const fixture=JSON.parse(await readFile('.local/recovery-fixture.json','utf8'));
assert(path.resolve(fixture.directory).startsWith(path.resolve('.local')+path.sep),'Synthetic ignored fixture directory only');
const credentials=process.env.DOCUMENTSTORAGE_TEST_USERNAME&&process.env.DOCUMENTSTORAGE_TEST_PASSWORD?{username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}:JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM||'/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1440,height:1100}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0;
const nav=name=>page.locator('.sidebar').getByRole('link',{name}).first().click();
const pending=()=>page.locator('.nav-secondary a[href="/recovery"] .count');
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
async function inspect(text){await page.locator('.recovery-operation').filter({hasText:text}).click();await page.getByRole('heading',{name:'Inspect before deciding.',exact:true}).waitFor();}
async function choose(decision){await page.getByLabel('Recovery decision',{exact:true}).selectOption(decision);await page.getByLabel('Reason for this decision',{exact:true}).fill('Checked both synthetic paths and preserved all original bytes.');await page.getByRole('checkbox',{name:'I checked both displayed paths',exact:false}).check();}
async function resolve(){await page.getByRole('button',{name:'Save recovery decision',exact:true}).click();await page.getByRole('status').filter({hasText:'Recovery decision recorded:'}).waitFor();}
try{
 await page.goto(base+'/recovery');await signIn(page,credentials);
 await page.goto(base+'/library');await ready(page);await page.locator('.recovery-banner').waitFor();await check(page.locator('.recovery-banner').textContent().then(t=>t.includes('Recovery browser archive')),'an unfinished operation identifies the paused storage throughout the workspace');
 await page.getByRole('button',{name:'New folder',exact:true}).waitFor();await check(page.getByRole('button',{name:'New folder',exact:true}).isDisabled(),'folder changes are disabled on recovery-blocked storage');
 await page.locator('.nav-secondary a[href="/recovery"]').click();await page.locator('.recovery-operation').first().waitFor();await inspect('Original → Recovered');await page.getByLabel('Recovery decision',{exact:true}).waitFor();
 await check(page.locator('.recovery-explanation').textContent().then(t=>t.includes('matches the original')),'browser inspection verifies complete folder contents against the saved fingerprint');
 await check(page.locator('.recovery-disk').count().then(n=>n===2),'inspection presents both physical locations and content fingerprints');
 await page.setViewportSize({width:390,height:844});await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'recovery comparison and decision controls fit mobile screens');await page.setViewportSize({width:1440,height:1100});await page.screenshot({path:'.local/recovery-workspace.png',fullPage:true});
 await check(page.getByRole('button',{name:'Save recovery decision',exact:true}).isDisabled(),'a recovery decision requires a reason and explicit identity confirmation');
 const target=path.join(fixture.directory,'Recovered/scan.txt');const original=await readFile(target,'utf8');const metadata=await stat(target);await writeFile(target,'X'.repeat(Buffer.byteLength(original)));await utimes(target,metadata.atime,metadata.mtime);
 await choose('Complete');await page.getByRole('button',{name:'Save recovery decision',exact:true}).click();await page.getByRole('status').filter({hasText:'changed since inspection'}).waitFor();
 await check(access(path.join(fixture.directory,'Original/scan.txt')).then(()=>true,()=>false),'stale browser inspection refuses a changed copy without touching either location');
 await writeFile(target,original);await utimes(target,metadata.atime,metadata.mtime);
 const marker=path.join(fixture.directory,'.documentstorage-root');await rename(marker,marker+'.offline');
 try {await page.getByRole('button',{name:'Inspect paths again',exact:true}).click();await page.locator('.recovery-explanation').filter({hasText:'Storage identity or availability could not be verified'}).waitFor();await check(page.getByLabel('Recovery decision',{exact:true}).count().then(n=>n===0),'offline storage cannot be acknowledged as missing files or resolved');}
 finally{await rename(marker+'.offline',marker);}
 await page.getByRole('button',{name:'Inspect paths again',exact:true}).click();await page.getByLabel('Recovery decision',{exact:true}).waitFor();await choose('Complete');await resolve();
 await check(readFile(target,'utf8').then(t=>t===original),'completed recovery preserves the verified destination bytes');
 await check(readFile(path.join(fixture.directory,'Original/scan.txt'),'utf8').then(t=>t===original),'completing a recovery preserves any remaining source copy');
 await pending().filter({hasText:'2'}).waitFor();await check(pending().textContent().then(t=>t.trim()==='2'),'resolving one operation keeps storage paused while other operations remain');
 await inspect('Changed.txt → Different.txt');await page.getByLabel('Recovery decision',{exact:true}).waitFor();
 await check(page.getByLabel('Recovery decision',{exact:true}).locator('option[value="Complete"]').count().then(n=>n===0),'a different destination has no completion choice in the browser');
 await choose('Cancel');await resolve();await check(readFile(path.join(fixture.directory,'Changed.txt'),'utf8').then(t=>t==='Synthetic source survives.'),'cancelling an ambiguous move preserves the source');
 await check(readFile(path.join(fixture.directory,'Different.txt'),'utf8').then(t=>t==='Synthetic different contents.'),'cancelling also preserves the distinct destination');
 await inspect('Old journal.txt → Legacy destination.txt');await page.getByLabel('Recovery decision',{exact:true}).waitFor();await choose('Complete');
 await page.locator('.recovery-inspector .error-text').filter({hasText:'explicitly adopting the destination without proof'}).waitFor();await check(true,'adopting an older journal explicitly discloses the missing fingerprint proof');await resolve();
 await page.getByRole('heading',{name:'No unfinished operations',exact:true}).waitFor();await pending().waitFor({state:'detached'});await page.goto(base+'/library');await ready(page);await check(page.locator('.recovery-banner').count().then(n=>n===0),'resolving the final operation clears the paused-storage banner');await page.goto(base+'/recovery');await ready(page);
 await page.locator('.recovery-history summary').click();await check(page.locator('.recovery-history article').count().then(n=>n===3),'all manual decisions retain their reasons, actor and operation receipts');
 await openFolder(page,'Recovered');await page.locator('.sticky-note .instruction-canonical').filter({hasText:'Synthetic recovery guidance survives.'}).waitFor();await check(true,'the chosen destination keeps the original folder notes');
 await newFolder(page,'Writes resumed');
 await check(access(path.join(fixture.directory,'Recovered/Writes resumed')).then(()=>true,()=>false),'normal folder writes resume after all recovery decisions commit');
 await page.goto(base+'/recovery');await ready(page);await page.getByRole('heading',{name:'No unfinished operations',exact:true}).waitFor();await check(true,'the recovery workspace supports a protected direct URL and page refresh');
 await check(errors.length===0,'recovery workflow has no browser script errors');console.log(`All ${checks} recovery browser checks passed.`);
}finally{await browser.close();}
