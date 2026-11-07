import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { signIn, connectStorage, openFolder, enterFolder, newFolder, useAsInbox, inbox, openInboxItem, toast, noOverflow } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5244';assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable local instance only');
const fixture=JSON.parse(await readFile('.local/preview-fixture.json','utf8'));
const c=process.env.DOCUMENTSTORAGE_TEST_USERNAME&&process.env.DOCUMENTSTORAGE_TEST_PASSWORD?{username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}:JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM||'/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});const page=await browser.newPage({viewport:{width:1600,height:1100}});let checks=0;const errors=[],external=[];page.on('pageerror',e=>errors.push(e.message));page.on('request',r=>{if(r.url().startsWith('http')&&new URL(r.url()).origin!==new URL(base).origin)external.push(r.url());});
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const root=page.locator('.document-preview');
async function select(name){await page.locator('.file-name').filter({has:page.locator('strong',{hasText:new RegExp('^'+name.replaceAll('.','\\.')+'$')})}).click();await root.locator('.document-preview-heading strong').filter({hasText:name}).waitFor();}
async function loaded(){await page.waitForFunction(()=>document.querySelector('.document-preview')?.dataset.loaded==='true');}
async function rendered(n=1){await page.waitForFunction(n=>document.querySelector(`.document-preview .page[data-page-number="${n}"] canvas`)?.width>0&&document.querySelector('.document-preview')?.dataset.rendered===String(n),n);}
try{
 await page.goto(base);await signIn(page,c);
 await connectStorage(page,base,'Preview test archive',fixture.directory);
 await select('statement.pdf');await loaded();await rendered();
 await check(page.evaluate(()=>{const c=document.querySelector('.page[data-page-number="1"] canvas');const d=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let ink=0;for(let i=0;i<d.length;i+=4)if(d[i]<100&&d[i+1]<100&&d[i+2]<100)ink++;return ink>100;}),'PDF preview renders actual document ink inside the application');
 await check(page.evaluate(()=>{const c=document.querySelector('.page[data-page-number="1"] canvas');const d=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let red=0;for(let i=0;i<d.length;i+=4)if(d[i]>150&&d[i+1]<100&&d[i+2]<100)red++;return red>20;}),'PDF preview includes filled form appearances without editable widgets');
 await check(root.locator('[data-pages]').textContent().then(t=>t==='of 3'),'multipage PDF reports its real page count');
 await root.getByRole('button',{name:'Next PDF page',exact:true}).click();await rendered(2);await check(root.getByLabel('PDF page',{exact:true}).inputValue().then(v=>v==='2'),'PDF page navigation renders the selected page');
 await root.getByLabel('PDF page',{exact:true}).fill('3');await root.getByLabel('PDF page',{exact:true}).press('Tab');await rendered(3);
 await root.getByRole('button',{name:'Zoom in',exact:true}).click();await root.getByRole('button',{name:'Rotate',exact:true}).click();await page.waitForFunction(()=>{const p=document.querySelector('.page[data-page-number="3"]');return p&&p.clientWidth>p.clientHeight;});await check(root.locator('.page[data-page-number="3"]').evaluate(p=>p.clientWidth>p.clientHeight),'PDF zoom and rotation update the rendered document');
 await root.getByRole('button',{name:'Fit width',exact:true}).click();await root.getByLabel('Find in PDF',{exact:true}).fill('Cedar');await root.getByLabel('Find in PDF',{exact:true}).press('Enter');await root.locator('[data-matches]').filter({hasText:/of 3 matches/}).waitFor();await check(true,'PDF search finds and highlights text across pages');
 await root.getByRole('button',{name:'Expand preview',exact:true}).click();await check(root.getAttribute('role').then(v=>v==='dialog'),'expanded preview opens an accessible in-app viewing surface');await page.keyboard.press('Escape');await check(root.getAttribute('aria-modal').then(v=>v===null),'Escape closes expanded preview and returns to the workspace');
 await select('photo.png');await loaded();await check(root.locator('img').evaluate(img=>img.naturalWidth===1200&&img.naturalHeight===800),'image preview decodes original dimensions');await root.getByRole('button',{name:'Zoom in',exact:true}).click();await root.getByRole('button',{name:'Rotate',exact:true}).click();await check(root.locator('img').getAttribute('style').then(s=>s.includes('rotate(90deg)')),'image zoom and rotation work without altering the source file');
 for(const name of ['photo.jpg','photo.webp','photo.gif','photo.bmp','photo.avif']){await select(name);await loaded();await check(root.locator('img').evaluate(img=>img.naturalWidth===1200),name+' previews inside the app');}
 await select('notes.md');await loaded();await check(root.locator('.source-preview').textContent().then(t=>t.includes('<script>window.previewPwned=true</script>')),'Markdown source preserves readable text without executing markup');await check(page.evaluate(()=>window.previewPwned===undefined),'document markup cannot execute in the app origin');
 await select('data.csv');await loaded();await check(root.locator('.source-preview').textContent().then(t=>t.includes('Cedar,2026-01-31,42.50')),'CSV preview retains document values');
 await select('unsupported.docx');await root.locator('[data-status]').filter({hasText:'contents do not match'}).waitFor();await check(root.getByRole('link',{name:'Download original',exact:true}).isVisible(),'damaged Office files have a clear original-download fallback');
 await select('fake.png');await root.locator('[data-status]').filter({hasText:'contents do not match'}).waitFor();await check(true,'mislabeled image contents produce an actionable preview error');
 await select('broken.pdf');await root.locator('[data-status]').filter({hasText:'could not be opened'}).waitFor();await check(true,'a malformed PDF fails visibly while keeping the workspace usable');
 await select('locked.pdf');await root.locator('[data-password-form]:not([hidden])').waitFor();await root.getByLabel('PDF password',{exact:true}).fill('fixture-pass');await root.getByRole('button',{name:'Unlock PDF',exact:true}).click();await loaded();await rendered();await check(true,'password-protected PDF opens using a browser-only password prompt');
 // Switch repeatedly while renders/fetches may still be pending.
 for(const name of ['statement.pdf','photo.png','notes.md','statement.pdf'])await select(name);await loaded();await rendered();await check(root.locator('[data-pages]').textContent().then(t=>t==='of 3'),'rapid document switching cancels old previews without replacing the latest selection');
 await check(page.evaluate(()=>getComputedStyle(document.querySelector('.sidebar')).backgroundColor==='rgb(196, 161, 115)'),'PDF styles preserve the cardboard application sidebar');
 await page.screenshot({path:'.local/preview-documents.png',fullPage:true});
 // The same renderer stays beside the inbox filing slip.
 const incoming='Incoming previews '+Date.now();await openFolder(page,'Preview test archive');await newFolder(page,incoming);await enterFolder(page,incoming);await useAsInbox(page);
 await page.locator('input[aria-label="Copy files to inbox"]').setInputFiles(fixture.directory+'/statement.pdf');await toast(page,'1 of 1 files added to the inbox.');await inbox(page);await openInboxItem(page,'statement.pdf');await loaded();await rendered();
 await check(page.getByLabel(/^Kind/).isVisible()&&await root.isVisible(),'the inbox keeps the actual PDF visible beside its filing slip');await page.screenshot({path:'.local/preview-intake.png',fullPage:true});
 await page.setViewportSize({width:390,height:844});await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'PDF preview and filing slip fit a mobile viewport');await root.getByRole('button',{name:'Expand preview',exact:true}).click();await check(root.locator('.preview-surface').evaluate(x=>x.clientHeight>250),'expanded mobile preview uses the available screen');await page.keyboard.press('Escape');
 await check(external.length===0,'PDF rendering and support assets stay on this app origin');await check(errors.length===0,'preview controls and rapid switching cause no browser script errors');console.log(`All ${checks} preview browser checks passed.`);
}finally{await browser.close();}
