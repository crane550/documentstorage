import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, openFolder, newFolder, toast, noOverflow, folderPicker, chooseFolder } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5242';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Use a disposable loopback catalog');
const directory=path.resolve('.local/folder-browser-'+Date.now());await mkdir(directory,{recursive:true});
await mkdir(path.join(directory,'Existing'));await writeFile(path.join(directory,'Existing/synthetic.txt'),'Synthetic folder management bytes.');
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});
const context=await browser.newContext({viewport:{width:1440,height:1050}});const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const exists=p=>access(path.join(directory,p)).then(()=>true,()=>false);
async function folderAction(target,name){await target.getByRole('button',{name:'Folder actions',exact:true}).click();await target.getByRole('menuitem',{name,exact:true}).click();}
try{
 await page.goto(base+'/folders');await signIn(page,await credentials());
 await connectStorage(page,base,'Folder test archive',directory);
 await openFolder(page,'Existing');
 await check(page.locator('.page-head h1').textContent().then(t=>t.includes('Existing')),'the sidebar tree opens a folder with its tools');
 await page.getByRole('button',{name:'Edit folder notes',exact:true}).click();await page.getByRole('textbox',{name:'Folder notes',exact:true}).fill('Synthetic folder guidance stays with its folder.');await page.getByRole('button',{name:'Save note',exact:true}).click();await toast(page,'Note saved.');
 await newFolder(page,'Child');await check(exists('Existing/Child'),'creating a folder creates the real directory');
 await openFolder(page,'Folder test archive');await newFolder(page,'Destination');
 await openFolder(page,'Existing');
 await folderAction(page,'Rename or move folder');await page.getByLabel('Name',{exact:true}).fill('Destination');await page.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await page.getByRole('alert').filter({hasText:'already exists'}).waitFor();
 await check(readFile(path.join(directory,'Existing/synthetic.txt'),'utf8').then(t=>t==='Synthetic folder management bytes.'),'a conflicting folder name leaves contents intact and the form open');
 await page.getByLabel('Name',{exact:true}).fill('../escape');await page.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await page.getByRole('alert').filter({hasText:'without slashes'}).waitFor();await check(true,'invalid names are rejected by the actual folder writer');
 await page.getByLabel('Name',{exact:true}).fill('Renamed');await page.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await check(exists('Renamed/Child'),'renaming a populated folder preserves the complete subtree');
 await page.locator('.page-head h1').filter({hasText:'Renamed'}).waitFor();
 await check(page.locator('.sticky-note .instruction-canonical').textContent().then(t=>t.includes('Synthetic folder guidance')),'folder notes survive a rename');
 await folderAction(page,'Rename or move folder');
 await page.getByRole('button',{name:'Destination folder',exact:true}).click();
 await folderPicker(page).getByRole('tree',{name:'Folders',exact:true}).waitFor();
 const options=await folderPicker(page).getByRole('treeitem').allTextContents();
 await check(!options.some(x=>x.trim()==='Renamed'||x.includes('Child')),'the destination tree excludes the folder itself and everything inside it');
 await chooseFolder(page,'Destination','Destination');
 await page.locator('.path-preview').filter({hasText:'Destination / Renamed'}).waitFor();
 await check(true,'the move form previews the full new path');
 await page.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 await check(readFile(path.join(directory,'Destination/Renamed/synthetic.txt'),'utf8').then(t=>t==='Synthetic folder management bytes.'),'moving a populated folder preserves document bytes');
 await page.locator('.crumbs').filter({hasText:'Destination'}).waitFor();
 await check(page.locator('.crumbs').textContent().then(t=>t.includes('Folder test archive')&&t.includes('Destination')),'the folder path updates after a move');
 // A second browser tab changes the same folder while the first keeps its form open.
 const address=page.url();
 await folderAction(page,'Rename or move folder');await page.getByLabel('Name',{exact:true}).fill('Old tab');
 const other=await context.newPage();await other.goto(address);await ready(other);await other.locator('.page-head h1').filter({hasText:'Renamed'}).waitFor();
 await folderAction(other,'Rename or move folder');await other.getByLabel('Name',{exact:true}).fill('New tab');await other.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await other.getByRole('dialog').waitFor({state:'hidden'});await other.close();
 await page.getByRole('dialog').getByRole('button',{name:'Save',exact:true}).click();await page.getByRole('alert').filter({hasText:'changed since you opened'}).waitFor();await page.getByRole('dialog').getByRole('button',{name:'Cancel',exact:true}).click();
 await check(exists('Destination/New tab/Child'),'a stale tab cannot rename a folder after another tab changes its path');
 await page.reload();await ready(page);await page.locator('.page-head h1').filter({hasText:'New tab'}).waitFor();await check(true,'folders support direct addresses and refresh');
 await openFolder(page,'Folder test archive');await check(page.getByRole('button',{name:'Folder actions',exact:true}).count().then(n=>n===0),'registered storage roots cannot be renamed or moved');
 await page.screenshot({path:'.local/folders-workspace.png',fullPage:true});await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'folder tools fit a mobile viewport');await check(errors.length===0,'folder tools have no browser script errors');
 console.log(`All ${checks} folder browser checks passed.`);
}finally{await browser.close();}
