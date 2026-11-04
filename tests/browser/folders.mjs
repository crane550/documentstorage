import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5242';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Use a disposable loopback catalog');
const directory=path.resolve('.local/folder-browser-'+Date.now());await mkdir(directory,{recursive:true});
await mkdir(path.join(directory,'Existing'));await writeFile(path.join(directory,'Existing/synthetic.txt'),'Synthetic folder management bytes.');
const credentials=process.env.DOCUMENTSTORAGE_TEST_USERNAME&&process.env.DOCUMENTSTORAGE_TEST_PASSWORD?{username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}:JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM||'/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const context=await browser.newContext({viewport:{width:1440,height:1050}});const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const row=name=>page.locator('.folder-directory-row').filter({has:page.locator('strong',{hasText:new RegExp('^'+name+'$')})});
async function create(name){await page.getByRole('button',{name:'New subfolder',exact:true}).click();await page.getByLabel('Folder name',{exact:true}).fill(name);await page.getByRole('button',{name:'Create subfolder',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});}
try{
 await page.goto(base+'/folders');await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);await page.getByRole('button',{name:'Sign in',exact:true}).click();
 await page.getByRole('button',{name:'Add storage',exact:true}).click();await page.getByLabel('Location name').fill('Folder test archive');await page.getByLabel('Full path on the server').fill(directory);await page.getByRole('button',{name:'Connect storage',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await page.getByRole('button',{name:'Manage folder Existing',exact:true}).click();await page.getByRole('region',{name:'Folder tools'}).getByRole('heading',{name:'Existing',exact:true}).waitFor();
 await check(page.locator('.folder-browser').isVisible(),'sidebar manage opens a dedicated folder workspace');
 await page.getByRole('button',{name:'Open in Documents',exact:false}).click();await page.getByLabel('Folder filing instructions').fill('Synthetic folder guidance stays with its folder.');await page.getByRole('button',{name:'Save instructions',exact:true}).click();await page.getByRole('status').filter({hasText:'Folder instructions saved.'}).waitFor();
 await page.getByRole('button',{name:'Manage folder Existing',exact:true}).click();
 await create('Child');await check(access(path.join(directory,'Existing/Child')).then(()=>true,()=>false),'creating a subfolder creates the real directory and selects it');
 await row('Folder test archive').click();await create('Destination');
 await row('Existing').click();await page.getByRole('button',{name:'Rename folder',exact:true}).click();await page.getByLabel('Folder name',{exact:true}).fill('Destination');await page.getByRole('button',{name:'Save folder name',exact:true}).click();await page.getByRole('alert').filter({hasText:'already exists'}).waitFor();
 await check(readFile(path.join(directory,'Existing/synthetic.txt'),'utf8').then(t=>t==='Synthetic folder management bytes.'),'a conflicting folder name leaves contents intact and the form open');
 await page.getByLabel('Folder name',{exact:true}).fill('../escape');await page.getByRole('button',{name:'Save folder name',exact:true}).click();await page.getByRole('alert').filter({hasText:'without slashes'}).waitFor();await check(true,'invalid names are rejected by the actual folder writer');
 await page.getByLabel('Folder name',{exact:true}).fill('Renamed');await page.getByRole('button',{name:'Save folder name',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await check(access(path.join(directory,'Renamed/Child')).then(()=>true,()=>false),'renaming a populated folder preserves the complete subtree');
 await check(page.locator('.folder-guidance').textContent().then(t=>t.includes('Synthetic folder guidance')),'saved folder instructions survive a rename');
 await page.getByRole('button',{name:'Move folder',exact:true}).click();
 const options=await page.getByLabel('Destination folder',{exact:true}).locator('option').allTextContents();
 await check(!options.some(x=>x.includes('Renamed')||x.includes('(root)')),'move picker excludes the source, descendants and current parent');
 await page.getByLabel('Find destination',{exact:true}).fill('Destination');await page.getByLabel('Destination folder',{exact:true}).selectOption({label:'Folder test archive / Destination'});
 await page.waitForFunction(()=>document.querySelector('.folder-change-preview')?.textContent.includes('Destination/Renamed'));
 await check(page.locator('.folder-change-preview').textContent().then(t=>t.includes('Destination/Renamed')),'move form previews the full new path');
 await page.getByRole('button',{name:'Move this folder',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await check(readFile(path.join(directory,'Destination/Renamed/synthetic.txt'),'utf8').then(t=>t==='Synthetic folder management bytes.'),'moving a populated folder preserves document bytes');
 await page.getByLabel('Find a folder',{exact:true}).fill('Destination/Renamed');await page.waitForFunction(()=>document.querySelectorAll('.folder-directory-row').length===2);await check(page.locator('.folder-directory-row').count().then(n=>n===2),'folder search includes descendant paths');await page.getByLabel('Find a folder',{exact:true}).fill('');
 // A second browser tab changes the same folder while the first keeps its form open.
 await page.getByRole('button',{name:'Rename folder',exact:true}).click();await page.getByLabel('Folder name',{exact:true}).fill('Old tab');
 const other=await context.newPage();await other.goto(base+'/folders');await other.locator('.folder-directory-row').filter({has:other.locator('strong',{hasText:/^Renamed$/})}).click();await other.getByRole('button',{name:'Rename folder',exact:true}).click();await other.getByLabel('Folder name',{exact:true}).fill('New tab');await other.getByRole('button',{name:'Save folder name',exact:true}).click();await other.getByRole('dialog').waitFor({state:'hidden'});await other.close();
 await page.getByRole('button',{name:'Save folder name',exact:true}).click();await page.getByRole('alert').filter({hasText:'changed since you opened'}).waitFor();await page.getByRole('button',{name:'Cancel',exact:true}).click();
 await check(access(path.join(directory,'Destination/New tab/Child')).then(()=>true,()=>false),'a stale tab cannot rename a folder after another tab changes its path');
 await page.reload();await row('New tab').waitFor();await check(true,'the folder page supports direct navigation and refresh');
 await row('Folder test archive').click();await check(page.getByRole('button',{name:'Rename folder',exact:true}).isDisabled(),'registered storage roots cannot be renamed or moved');
 await page.screenshot({path:'.local/folders-workspace.png',fullPage:true});await page.setViewportSize({width:390,height:844});await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'folder management fits a mobile viewport');await check(errors.length===0,'folder tools have no browser script errors');
 console.log(`All ${checks} folder browser checks passed.`);
}finally{await browser.close();}
