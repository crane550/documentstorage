import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
const base=process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5241';
assert(['127.0.0.1','localhost','[::1]'].includes(new URL(base).hostname),'Disposable local instance only');
const directory=path.resolve('.local/drop-storage-'+Date.now());await mkdir(directory,{recursive:true});
const credentials=process.env.DOCUMENTSTORAGE_TEST_USERNAME&&process.env.DOCUMENTSTORAGE_TEST_PASSWORD?{username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}:JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM||'/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1440,height:1000}});const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
async function drop(files){await page.evaluate(files=>{const dt=new DataTransfer();for(const f of files)dt.items.add(new File([f.text],f.name,{type:'text/plain'}));document.body.dispatchEvent(new DragEvent('dragenter',{bubbles:true,cancelable:true,dataTransfer:dt}));document.body.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:dt}));},files);}
async function folder(name){await page.getByRole('button',{name:'New folder',exact:true}).click();await page.getByLabel('Name',{exact:true}).fill(name);await page.getByRole('button',{name:'Create folder',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});}
async function root(){await page.locator('.tree-label').filter({hasText:'Drop test archive'}).click();await page.locator('.breadcrumbs').filter({hasText:'Drop test archive'}).waitFor();}
try{
 await page.goto(base);await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);await page.getByRole('button',{name:'Sign in',exact:true}).click();
 await page.waitForFunction(()=>document.querySelector('.intake-upload')?.dataset.dropReady==='true');
 await drop([{name:'no-target.txt',text:'Synthetic'}]);await page.locator('[data-drop-feedback]').filter({hasText:'Choose an available intake folder before dropping files.'}).waitFor();
 await check(new URL(page.url()).pathname==='/', 'dropping without intake preserves the app and explains setup');
 await page.getByRole('button',{name:'Add storage',exact:true}).click();await page.getByLabel('Location name').fill('Drop test archive');await page.getByLabel('Full path on the server').fill(directory);await page.getByRole('button',{name:'Connect storage',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await folder('Incoming A');await page.locator('.file-name').filter({hasText:'Incoming A'}).click();await page.getByRole('button',{name:'Use as intake',exact:true}).click();await page.getByRole('button',{name:'Stop using as intake',exact:true}).waitFor();
 await page.waitForFunction(()=>document.querySelector('.intake-upload').dataset.ready==='true');
 await check(page.getByLabel('Intake destination',{exact:true}).locator('option:checked').textContent().then(t=>t.includes('Incoming A')),'a single intake folder is selected automatically');
 await page.locator('.main-nav').getByRole('button',{name:'Activity',exact:true}).click();
 const source=path.join(directory,'browser-original.txt');const text='Synthetic browser source: copper lantern.';await writeFile(source,text);
 await drop([{name:'scan-one.txt',text:await readFile(source,'utf8')},{name:'scan-two.txt',text:'Synthetic second file.'}]);
 await page.getByRole('status').filter({hasText:'2 of 2 files copied to intake.'}).waitFor();
 await check(readFile(path.join(directory,'Incoming A/scan-one.txt'),'utf8').then(t=>t===text),'a window drop from another view writes unchanged bytes into intake');
 await check(readFile(source,'utf8').then(t=>t===text),'copying keeps the original source file intact');
 await drop([{name:'scan-one.txt',text:'Must never overwrite'},{name:'scan-three.txt',text:'Third synthetic file.'}]);
 await page.getByRole('status').filter({hasText:'1 of 2 files copied to intake.'}).waitFor();
 await check(readFile(path.join(directory,'Incoming A/scan-one.txt'),'utf8').then(t=>t===text),'duplicate filenames never overwrite an existing document');
 await check(page.getByLabel('Intake upload results').textContent().then(t=>t.includes('already exists')&&t.includes('scan-three.txt')),'partial batch results distinguish collisions from successful copies');
 await drop(Array.from({length:21},(_,i)=>({name:'too-many-'+i+'.txt',text:'Synthetic'})));await page.locator('[data-drop-feedback]').filter({hasText:'Choose at most 20 files per batch. No files were copied.'}).waitFor();
 await check(access(path.join(directory,'Incoming A/too-many-0.txt')).then(()=>false,()=>true),'oversized batches are rejected before any file is copied');
 await page.evaluate(()=>{const dt=new DataTransfer();dt.items.add(new File([new Uint8Array(100*1024*1024+1)],'too-large.bin'));document.body.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:dt}));});
 await page.locator('[data-drop-feedback]').filter({hasText:'Each file must be 100 MB or smaller. No files were copied.'}).waitFor();
 await check(access(path.join(directory,'Incoming A/too-large.bin')).then(()=>false,()=>true),'files over 100 MB are rejected before transfer');
 await page.evaluate(()=>{const e=new Event('drop',{bubbles:true,cancelable:true});Object.defineProperty(e,'dataTransfer',{value:{types:['Files'],items:[{webkitGetAsEntry:()=>({isDirectory:true})}],files:[]}});document.body.dispatchEvent(e);});
 await page.locator('[data-drop-feedback]').filter({hasText:'Drop individual files, not folders. No files were copied.'}).waitFor();await check(true,'folder drops are rejected explicitly');
 await root();await folder('Incoming B');await page.locator('.file-name').filter({hasText:'Incoming B'}).click();await page.getByRole('button',{name:'Use as intake',exact:true}).click();await page.getByRole('button',{name:'Stop using as intake',exact:true}).waitFor();
 await page.reload();await page.getByLabel('Intake destination',{exact:true}).waitFor();await page.waitForFunction(()=>document.querySelector('#intake-upload-target').options.length===3);
 await check(page.getByLabel('Intake destination',{exact:true}).inputValue().then(v=>v==='00000000-0000-0000-0000-000000000000'),'multiple intake folders require a destination instead of guessing');
 await page.getByLabel('Intake destination',{exact:true}).selectOption({label:'Drop test archive / Incoming B'});
 await page.waitForFunction(()=>document.querySelector('.intake-upload').dataset.ready==='true');
 await drop([{name:'selected-target.txt',text:'Correct second intake.'}]);await page.getByRole('status').filter({hasText:'1 of 1 files copied to intake.'}).waitFor();
 await check(readFile(path.join(directory,'Incoming B/selected-target.txt'),'utf8').then(t=>t==='Correct second intake.'),'the explicit intake selection controls where dropped files go');
 await page.locator('input[aria-label="Copy files to intake"]').setInputFiles({name:'picker.txt',mimeType:'text/plain',buffer:Buffer.from('Keyboard picker synthetic.')});
 await page.getByLabel('Intake upload results').getByText('picker.txt',{exact:true}).waitFor();await page.waitForFunction(()=>document.querySelector('.intake-upload').dataset.ready==='true');
 await check(readFile(path.join(directory,'Incoming B/picker.txt'),'utf8').then(t=>t==='Keyboard picker synthetic.'),'the file chooser provides the same accessible copy workflow');
 await page.locator('.main-nav').getByRole('button',{name:/^Intake/}).click();await page.locator('.intake-item').filter({hasText:'selected-target.txt'}).waitFor();await check(true,'copied files appear immediately in the intake queue');
 await page.setViewportSize({width:390,height:844});await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'intake copy controls and results fit mobile screens');
 await check(errors.length===0,'drag-and-drop does not cause browser script errors');console.log(`All ${checks} intake drop browser checks passed.`);
}finally{await browser.close();}
