import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, noOverflow, fileRow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5253';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname), 'Fresh loopback catalog only');
const directory = path.resolve('.local/office-browser-' + Date.now());
execFileSync('dotnet', ['tests/DocumentStorage.Checks/bin/Release/net10.0/DocumentStorage.Checks.dll', '--office-fixtures', directory], { stdio: 'pipe' });
const names = ['Synthetic policy.doc','Synthetic workbook.xls','Synthetic training.pptx','Synthetic rich text.rtf','Synthetic policy.odt','Synthetic workbook.ods'];
const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox','--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 1050 } });
const errors = []; page.on('pageerror', error => errors.push(error.message));
let checks = 0;
async function check(value,label) { assert(await value,label);checks++;console.log('PASS '+label); }
const preview = page.locator('.document-preview');
async function open(name) { await fileRow(page,name).click();await preview.locator('.document-preview-heading > strong').filter({hasText:name}).waitFor(); }
try {
    await page.goto(base);await signIn(page,await credentials());
    await connectStorage(page,base,'Synthetic Office archive',directory);
    for (const name of names) {
        await open(name);
        const kind = name.endsWith('.pptx') ? '.slide-content' : /\.(xls|ods)$/.test(name) ? '.sheet-table' : '.document-text';
        await preview.locator(kind).filter({hasText:/Synthetic/}).waitFor({timeout:90000});
        await check(true,name+' has an actual extracted-content preview');
        const download = await page.context().request.get(new URL(await preview.getByRole('link',{name:'Download original',exact:true}).getAttribute('href'),base).href);
        await check(download.ok() && Buffer.compare(await download.body(),await readFile(path.join(directory,name)))===0,name+' preserves the original download');
    }
    await open('Synthetic training.pptx');
    await preview.locator('.slide-content').filter({hasText:'NOTE-TOKEN'}).waitFor();
    await check(preview.getByRole('heading',{name:'Speaker notes',exact:true}).isVisible(),'PowerPoint includes speaker notes with the correct first slide');
    await preview.getByRole('button',{name:'Next slide',exact:true}).click();
    await check(preview.locator('.slide-content').textContent().then(text=>text.includes('SECOND-SLIDE')&&!text.includes('NOTE-TOKEN')),'slide navigation switches saved content and clears previous notes');
    await preview.getByLabel('Presentation slide',{exact:true}).selectOption('0');
    await check(preview.locator('.slide-content').textContent().then(text=>text.includes('FIRST-SLIDE')&&text.includes('NOTE-TOKEN')),'slide selector follows presentation order');
    await preview.getByRole('button',{name:'Expand preview',exact:true}).click();
    await page.screenshot({path:'.local/office-slides-desktop.png'});
    await preview.getByRole('button',{name:'Close expanded preview',exact:true}).click();
    await open('Synthetic rich text.rtf');
    await preview.locator('.document-text').filter({hasText:'Café'}).waitFor();
    await check(preview.locator('.document-text').textContent().then(text=>text.includes('你好')&&text.includes('😀')&&text.includes('Visible field')&&!text.includes('SECRET')),'RTF preview shows Unicode and visible field results');
    await open('Synthetic workbook.ods');
    await preview.getByRole('tab',{name:'Notes',exact:true}).click();
    await check(preview.getByRole('cell',{name:'<script>window.odsMustNotRun=true</script>',exact:true}).isVisible(),'OpenDocument worksheet tabs show literal markup');
    await check(page.evaluate(()=>!window.odfMustNotRun&&!window.odsMustNotRun&&!window.rtfMustNotRun&&!window.pptMustNotRun&&document.querySelectorAll('.structured-preview script').length===0),'all new previews keep document markup inert');
    await page.goto(base+'/library?q=NOTE-TOKEN');await ready(page);await fileRow(page,'Synthetic training.pptx').waitFor();await check(true,'search finds PowerPoint speaker notes');
    await page.goto(base+'/library?q=POLICY-TOKEN');await ready(page);await fileRow(page,'Synthetic policy.odt').waitFor();await fileRow(page,'Synthetic policy.doc').waitFor();await check(true,'search finds both native and converted policy documents');
    await page.goto(base+'/settings?tab=formats');await ready(page);
    await page.getByRole('button',{name:'Check Office converter',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Legacy Office conversion is ready.'}).waitFor();await check(true,'settings verifies the service-visible converter');
    await check(page.locator('.format-card').filter({hasText:'PowerPoint presentation'}).textContent().then(text=>text.includes('.pptx')&&text.includes('Slide text and notes')),'registered presentation capabilities match the available viewer');
    await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'new format settings fit a phone');
    await page.goto(base+'/library?q=NOTE-TOKEN');await ready(page);await open('Synthetic training.pptx');await preview.locator('.slide-content').filter({hasText:'NOTE-TOKEN'}).waitFor();
    await check(noOverflow(page),'slide navigation and notes fit a phone');await page.screenshot({path:'.local/office-slides-phone.png'});
    await check(errors.length===0 && !(await page.locator('#blazor-error-ui').isVisible()),'new formats produce no browser or circuit errors');
    console.log(`All ${checks} additional Office browser checks passed.`);
} catch(error) { await page.screenshot({path:'.local/office-formats-failure.png',fullPage:true}).catch(()=>{});throw error; }
finally {await browser.close();}
