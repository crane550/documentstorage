import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5240';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname), 'Use only a disposable local instance');
const directory = path.resolve('.local/intake-browser-storage');
await mkdir(directory,{recursive:true});
const credentials = process.env.DOCUMENTSTORAGE_TEST_USERNAME && process.env.DOCUMENTSTORAGE_TEST_PASSWORD
    ? {username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}
    : JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser = await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM || '/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const page = await browser.newPage({viewport:{width:1440,height:1050}});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
try {
    await page.goto(base);
    await page.getByLabel('Username',{exact:true}).fill(credentials.username);
    await page.getByLabel('Password',{exact:true}).fill(credentials.password);
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});
    const root = page.locator('.tree-label').filter({hasText:'Intake test archive'});
    if (await root.count()===0) {
        await page.getByRole('button',{name:'Add storage',exact:true}).click();
        await page.getByLabel('Location name').fill('Intake test archive');
        await page.getByLabel('Full path on the server').fill(directory);
        await page.getByRole('button',{name:'Connect storage',exact:true}).click();
        await page.getByRole('dialog').waitFor({state:'hidden'});
    } else {await root.click();await page.locator('.breadcrumbs').filter({hasText:'Intake test archive'}).waitFor();}
    const stamp=Date.now().toString();
    const incoming='Incoming '+stamp, archive='Filed '+stamp, source='scan-'+stamp+'.txt';
    for (const folder of [incoming,archive]) {
        await page.getByRole('button',{name:'New folder',exact:true}).click();
        await page.getByLabel('Name',{exact:true}).fill(folder);
        await page.getByRole('button',{name:'Create folder',exact:true}).click();
        await page.getByRole('dialog').waitFor({state:'hidden'});
    }
    await page.locator('.file-name').filter({hasText:incoming}).click();
    await page.getByRole('button',{name:'Use as intake',exact:true}).click();
    await page.getByRole('button',{name:'Stop using as intake',exact:true}).waitFor();
    await check(Promise.resolve(true),'folder is designated as intake through the real UI');
    const content='Synthetic monthly bank statement. Statement ends 2026-10-31. Account 1234. Balance 42.50.';
    await page.getByLabel('Upload documents').setInputFiles({name:source,mimeType:'text/plain',buffer:Buffer.from(content)});
    await page.locator('.file-name').filter({hasText:source}).waitFor();
    // The production stability window is intentional, rather than a renderer synchronization delay.
    await new Promise(resolve=>setTimeout(resolve,5500));
    await page.getByRole('button',{name:'Rescan storage',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Storage checked.'}).waitFor();
    await page.locator('.main-nav').getByRole('button',{name:/^Intake/}).click();
    await page.locator('.intake-item').filter({hasText:source}).click();
    await check(page.locator('.review-text pre').first().textContent().then(t=>t.includes('42.50')),'intake review shows actual extracted document text');
    const type='statement_'+stamp, bank='Cedar-'+stamp, suffix=stamp.slice(-4);
    await page.getByLabel('Document type',{exact:true}).fill(type);
    for(const [index,value] of [bank,suffix,'2026-10-31'].entries()) await page.getByLabel('Fact value',{exact:true}).nth(index).fill(value);
    await page.getByLabel('I checked these facts against the document').check();
    await page.getByRole('button',{name:'Save details',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Document details saved.'}).waitFor();
    await page.getByRole('button',{name:'Filing rules',exact:true}).click();
    await page.getByLabel('Rule name',{exact:true}).fill('Cedar statements '+stamp);
    await page.getByLabel('Document type',{exact:true}).fill(type);
    await page.getByLabel('Condition value',{exact:true}).fill(bank);
    await page.getByLabel('Destination folder',{exact:true}).selectOption({label:'Intake test archive / '+archive});
    await page.getByRole('button',{name:'Save rule',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Rule version saved.'}).waitFor();
    await page.getByRole('button',{name:'Test against intake',exact:true}).click();
    const simulation=page.locator('.simulation-row').filter({hasText:source});
    await simulation.getByText('Matches',{exact:true}).waitFor();
    await check(readFile(path.join(directory,incoming,source),'utf8').then(t=>t===content),'testing a paused rule previews the name without moving the source');
    await page.getByLabel('Enable this rule for document previews').check();
    await page.getByRole('button',{name:'Save new version',exact:true}).click();
    await page.locator('.rule-list-item').filter({hasText:'Cedar statements '+stamp}).getByText('Enabled',{exact:true}).waitFor();
    await check(page.locator('.rule-history summary').textContent().then(t=>t.includes('2')),'enabling the rule retains its version history');
    await page.screenshot({path:'.local/filing-rules.png',fullPage:true});
    await page.locator('.main-nav').getByRole('button',{name:/^Intake/}).click();
    await page.locator('.intake-item').filter({hasText:source}).click();
    await page.getByRole('button',{name:'Save & preview',exact:true}).click();
    const target='2026-10-31 - '+bank+' - '+suffix+'.txt';
    await page.locator('.filing-preview.ready').getByRole('heading',{name:target,exact:true}).waitFor();
    await check(page.locator('.preview-destination').textContent().then(t=>t.includes(archive+'/'+target)),'live preview displays the exact destination and filename');
    await page.screenshot({path:'.local/intake-preview.png',fullPage:true});
    await page.getByRole('button',{name:'Rename & file',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Document renamed and filed.'}).waitFor();
    await check(readFile(path.join(directory,archive,target),'utf8').then(t=>t===content),'approved filing moves the actual document without altering its bytes');
    await check(page.locator('.intake-item').filter({hasText:source}).count().then(n=>n===0),'filed document leaves the intake queue');
    await page.getByRole('button',{name:'Activity',exact:true}).click();
    await page.locator('.activity-row').filter({hasText:source}).waitFor();
    await check(page.locator('.activity-row').filter({hasText:source}).textContent().then(t=>t.includes('Completed')),'filing is recorded in the existing operation history');
    await page.getByRole('button',{name:'Filing rules',exact:true}).click();
    await page.locator('.rule-list-item').filter({hasText:'Cedar statements '+stamp}).click();
    await page.setViewportSize({width:390,height:844});
    await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'rule editor and new navigation fit the mobile viewport');
    await check(errors.length===0,'intake and rule editor have no browser JavaScript errors');
    console.log(`All ${checks} intake browser checks passed.`);
} finally {await browser.close();}
