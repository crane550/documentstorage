import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, noOverflow } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5255';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable loopback catalog only');
const directory=path.resolve('.local/image-browser-'+Date.now());execFileSync('python3',['tests/browser/image-fixture.py',directory]);
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1500,height:1100}});const errors=[];page.on('pageerror',error=>errors.push(error.message));let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const preview=page.locator('.document-preview');
async function open(name){await page.locator('.file-name').filter({has:page.locator('strong',{hasText:new RegExp('^'+name.replaceAll('.','\\.')+'$')})}).click();await preview.locator('.document-preview-heading strong').filter({hasText:name}).waitFor();}
async function loaded(){await page.waitForFunction(()=>document.querySelector('.document-preview')?.dataset.loaded==='true');}
async function ink(){return await preview.locator('img').evaluate(image=>{const c=document.createElement('canvas');c.width=image.naturalWidth;c.height=image.naturalHeight;const ctx=c.getContext('2d');ctx.drawImage(image,0,0);const p=ctx.getImageData(0,0,c.width,c.height).data;let ink=0;for(let i=0;i<p.length;i+=4)if(p[i]<100&&p[i+1]<100&&p[i+2]<100)ink++;return ink;});}
try{
 await page.goto(base);await signIn(page,await credentials());await connectStorage(page,base,'Synthetic image archive',directory);
 await open('Synthetic statement.png');await loaded();await check((await ink())>10000,'PNG preview shows actual printed document pixels');
 await preview.getByRole('button',{name:'Rotate',exact:true}).click();const initialBlob=await preview.locator('img').getAttribute('src');
 await page.locator('.metadata > div').filter({has:page.locator('dt',{hasText:/^Text$/})}).locator('dd').filter({hasText:/^Indexed$/}).waitFor({timeout:90000});
 await check((await preview.locator('img').getAttribute('src'))===initialBlob&&(await preview.locator('img').getAttribute('style')).includes('rotate(90deg)'),'background OCR completion preserves the open image and its rotation');

 const download=await page.context().request.get(new URL(await preview.getByRole('link',{name:'Download original',exact:true}).getAttribute('href'),base).href);
 await check(download.ok()&&Buffer.compare(await download.body(),await readFile(path.join(directory,'Synthetic statement.png')))===0,'image OCR and previews preserve the original downloadable bytes');
 await open('Synthetic multipage.tiff');await loaded();await check(preview.locator('[data-pages]').textContent().then(text=>text==='of 2'),'TIFF preview exposes both document pages');
 const first=await ink();await preview.getByRole('button',{name:'Next image page',exact:true}).click();await page.waitForFunction(()=>document.querySelector('.document-preview [data-page]')?.value==='2'&&document.querySelector('.document-preview')?.dataset.loaded==='true');
 await check((await ink())>1000&&(await ink())<first,'TIFF navigation renders the distinct second page as real image pixels');
 await preview.getByRole('button',{name:'Rotate',exact:true}).click();await check(preview.locator('img').getAttribute('style').then(style=>style.includes('rotate(90deg)')),'converted image pages keep zoom and rotation controls');
 await preview.getByRole('button',{name:'Expand preview',exact:true}).click();await check(preview.getAttribute('role').then(role=>role==='dialog'),'multipage images expand inside the app');await page.keyboard.press('Escape');
 await page.screenshot({path:'.local/image-tiff-desktop.png',fullPage:false});
 for(const name of ['Synthetic phone.heic','Synthetic multipage.tiff','Synthetic statement.png','Synthetic multipage.tiff'])await open(name);await loaded();await check(preview.locator('[data-pages]').textContent().then(text=>text==='of 2'),'rapid switching disposes converted image loads and keeps the latest TIFF selected');
 await open('Synthetic phone.heic');await loaded();await check((await ink())>10000,'HEIC phone images display inside the app through the bundled decoder');
 await open('Damaged.tiff');await preview.locator('[data-status]').filter({hasText:/could not be decoded/}).waitFor();await check(preview.getByRole('link',{name:'Download original',exact:true}).isVisible(),'a damaged image has a controlled error and original download fallback');
 await page.goto(base+'/settings?tab=formats');await ready(page);await page.getByRole('button',{name:'Check OCR engine',exact:true}).click();await page.getByRole('status').filter({hasText:'Ready for local image OCR.'}).waitFor();
 await check(page.locator('.format-card').filter({hasText:'TIFF document'}).textContent().then(text=>text.includes('Extracted text')&&text.includes('Multipage')),'format capabilities describe TIFF preview and OCR honestly');
 await check(page.getByText('Scan orientation detection available',{exact:false}).isVisible(),'the OCR engine check reports installed orientation support');
 let found=false;for(let i=0;i<60;i++){await page.goto(base+'/library?q='+encodeURIComponent('CEDAR BANK 1234'));await ready(page);if(await page.locator('.file-name').filter({hasText:'Synthetic statement.png'}).count()){found=true;break;}await page.waitForTimeout(1500);}
 await check(found,'real background OCR makes an image searchable without an AI call or manual transcription');
 await open('Synthetic statement.png');await page.locator('.review-text summary').click();await check(page.locator('.review-text pre').textContent().then(text=>text.includes('2500.00')),'saved image OCR is readable in the normal Library document details');
 await page.reload();await ready(page);await page.locator('.review-text summary').click();await check(page.locator('.review-text pre').textContent().then(text=>text.includes('1234')),'the extracted image text persists across page reloads');
 await page.setViewportSize({width:390,height:844});await page.waitForFunction(()=>document.querySelector('.sidebar').getBoundingClientRect().right<=0);await check(noOverflow(page),'image preview and OCR details fit a phone');await page.screenshot({path:'.local/image-ocr-phone.png',fullPage:false});
 await page.goto(base+'/library');await ready(page);await check(noOverflow(page),'the full image list, including failed extraction statuses, fits a phone');
 await check(errors.length===0&&!(await page.locator('#blazor-error-ui').isVisible()),'image decoding, OCR and navigation produce no browser or circuit errors');
 console.log(`All ${checks} image OCR browser checks passed.`);
}catch(error){await page.screenshot({path:'.local/image-browser-failure.png',fullPage:true}).catch(()=>{});throw error;}finally{await browser.close();}
