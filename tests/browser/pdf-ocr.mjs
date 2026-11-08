import {chromium} from 'playwright';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {readFile,copyFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import {credentials,chromiumPath,signIn,ready,connectStorage,noOverflow} from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5258';assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable loopback app only');
const fixtures=path.resolve('.local/pdf-ocr-fixtures-'+Date.now());execFileSync('python3',['tests/browser/pdf-ocr-fixture.py',fixtures]);
const directory=path.resolve('.local/pdf-ocr-browser-'+Date.now());await mkdir(directory,{recursive:true});
for(const name of ['Scanned statement.pdf','Mixed policy.pdf','Rotated scan.pdf','Footer scan.pdf','Native text.pdf','Locked scan.pdf','Broken.pdf'])await copyFile(path.join(fixtures,name),path.join(directory,name));
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});const page=await browser.newPage({viewport:{width:1500,height:1100}});const errors=[];page.on('pageerror',error=>errors.push(error.message));let checks=0;
const preview=page.locator('.document-preview');async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
async function open(name){await page.locator('.file-name').filter({has:page.locator('strong',{hasText:new RegExp('^'+name.replaceAll('.','\\.')+'$')})}).click();await preview.locator('.document-preview-heading strong').filter({hasText:name}).waitFor();}
async function rendered(n=1){await page.waitForFunction(n=>document.querySelector(`.document-preview .page[data-page-number="${n}"]`)?.dataset.rendered==='true',n);}
async function ink(n=1){return await preview.locator(`.page[data-page-number="${n}"] canvas`).evaluate(c=>{const pixels=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let count=0;for(let i=0;i<pixels.length;i+=4)if(pixels[i]<100&&pixels[i+1]<100&&pixels[i+2]<100)count++;return count;});}
try{
 await page.goto(base);await signIn(page,await credentials());await connectStorage(page,base,'Synthetic scanned PDF archive',directory);
 await open('Scanned statement.pdf');await rendered();await check((await ink())>1000,'an image-only PDF previews actual scanned text pixels while OCR runs separately');
 const download=await page.context().request.get(new URL(await preview.getByRole('link',{name:'Download original',exact:true}).getAttribute('href'),base).href);
 await check(download.ok()&&Buffer.compare(await download.body(),await readFile(path.join(directory,'Scanned statement.pdf')))===0,'OCR leaves the downloadable original PDF byte-for-byte unchanged');
 await page.goto(base+'/settings?tab=formats');await ready(page);await page.getByRole('button',{name:'Check PDF tools',exact:true}).click();await page.getByRole('status').filter({hasText:'PDF text extraction and scanned-page rendering are ready.'}).waitFor();
 await check(page.locator('.format-card').filter({hasText:/^PDF/}).textContent().then(text=>text.includes('mixed PDFs')&&text.includes('local OCR')),'PDF capabilities explain embedded-text and scanned-page handling');
 await page.getByRole('button',{name:'Check OCR engine',exact:true}).click();await page.getByRole('status').filter({hasText:'Ready for local image OCR.'}).waitFor();await check(true,'the app verifies both Poppler and the real local OCR engine');
 let found=false;for(let i=0;i<60;i++){await page.goto(base+'/library?q='+encodeURIComponent('CEDAR BANK 1234'));await ready(page);if(await page.locator('.file-name').filter({hasText:'Scanned statement.pdf'}).count()){found=true;break;}await page.waitForTimeout(1500);}
 await check(found,'background OCR makes scanned PDF content searchable without an AI call');await open('Scanned statement.pdf');await page.locator('.review-text summary').click();
 await check(page.locator('.review-text pre').textContent().then(text=>text.includes('2500.00')),'saved PDF OCR appears in the normal document text details');
 await page.reload();await ready(page);await page.locator('.review-text summary').click();await check(page.locator('.review-text pre').textContent().then(text=>text.includes('1234')),'PDF OCR remains saved across a full browser reload');
 found=false;for(let i=0;i<60;i++){await page.goto(base+'/library?q='+encodeURIComponent('Emergency exits'));await ready(page);if(await page.locator('.file-name').filter({hasText:'Mixed policy.pdf'}).count()){found=true;break;}await page.waitForTimeout(1500);}
 await check(found,'a text cover does not hide scanned policy content on later pages');await open('Mixed policy.pdf');await rendered();await preview.getByRole('button',{name:'Next PDF page',exact:true}).click();await rendered(2);await check((await ink(2))>1000,'the original scanned policy page remains accessible at its correct PDF page number');
 await page.locator('.extraction-summary summary').click();await check(page.locator('.extraction-summary').textContent().then(text=>text.includes('Existing text')&&text.includes('Local OCR')&&text.includes('word score')),'the document exposes per-page text sources and OCR quality for verification');
 await page.screenshot({path:'.local/pdf-ocr-mixed-desktop.png',fullPage:false});
 await page.goto(base+'/library?q='+encodeURIComponent('NativeTextSentinel 52790'));await ready(page);await page.locator('.file-name').filter({hasText:'Native text.pdf'}).waitFor();await check(true,'existing embedded PDF text still feeds search alongside OCR results');
 await page.goto(base+'/library');await ready(page);await open('Locked scan.pdf');await preview.locator('[data-password-form]:not([hidden])').waitFor();await check(true,'password-protected PDFs keep their browser-only preview password prompt');
 await page.setViewportSize({width:390,height:844});await page.waitForFunction(()=>document.querySelector('.sidebar').getBoundingClientRect().right<=0);await check(noOverflow(page),'scanned PDF previews and status messages fit a phone');await page.screenshot({path:'.local/pdf-ocr-phone.png',fullPage:false});
 await check(errors.length===0&&!(await page.locator('#blazor-error-ui').isVisible()),'PDF OCR, preview and navigation cause no browser or circuit errors');
 console.log(`All ${checks} scanned PDF OCR browser checks passed.`);
}catch(error){await page.screenshot({path:'.local/pdf-ocr-browser-failure.png',fullPage:true}).catch(()=>{});throw error;}finally{await browser.close();}
