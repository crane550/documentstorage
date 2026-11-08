import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, noOverflow } from './ui.mjs';

const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5256';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable local catalog only');
const directory=path.resolve('.local/naming-browser-'+Date.now());await mkdir(path.join(directory,'Inbox'),{recursive:true});
const names=['Bank statement October.txt','Bank statement November.txt','Bank statement December.txt','Bank statement January.txt'];
for(let i=0;i<names.length;i++)await writeFile(path.join(directory,'Inbox',names[i]),`Preference Bank\nBank statement\nDate: ${i===3?'2027-01-31':`2026-${i+10}-28`}\nAccount ending 1234\nSynthetic statement sequence ${i+1}.`);
await writeFile(path.join(directory,'Inbox','Acme invoice September.txt'),'Acme Supply\nInvoice 4488\nDate: 2026-09-28\nSynthetic purchase total 42.50.');
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1450,height:1050}});const errors=[];page.on('pageerror',error=>errors.push(error.message));
let checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const slip=page.locator('.sort-slip'),nameChoice=slip.getByRole('group',{name:'How the name is chosen',exact:true});
async function open(name){await page.locator('.sort-row').filter({has:page.locator('strong',{hasText:name})}).locator('.intake-item').click();await slip.locator('h2').filter({hasText:name}).waitFor();}
async function selected(mode){await nameChoice.getByRole('button',{name:mode,exact:true}).waitFor();return await nameChoice.getByRole('button',{name:mode,exact:true}).getAttribute('aria-pressed')==='true';}
async function file(){await slip.getByRole('button',{name:'Approve & file',exact:false}).click();await toast(page,'Filed as');}
try{
 await page.goto(base);await signIn(page,await credentials());await connectStorage(page,base,'Naming preference archive',directory);
 await openFolder(page,'Inbox');await useAsInbox(page);await settleAndRescan(page);await page.goto(base+'/');await ready(page);
 await open(names[0]);await check(selected('Original'),'a descriptive filename starts with Original before a kind has a preference');
 await page.getByLabel(/^From/).fill('Preference Bank');await page.getByLabel(/^From/).press('Tab');
 await page.locator('#sort-field-account_ending').fill('1234');await page.locator('#sort-field-account_ending').press('Tab');
 await nameChoice.getByRole('button',{name:'Built',exact:true}).click();
 await slip.getByText(/After approval, Built becomes the default/).waitFor();await file();
 await check(access(path.join(directory,'Banking/Preference Bank/2026/2026-10 Preference Bank statement 1234.txt')).then(()=>true,()=>false),'approving Built files under the generated name');
 await open(names[1]);await check(selected('Built'),'the next bank statement starts with the remembered Built choice');
 await page.reload();await ready(page);await open(names[1]);await check(selected('Built'),'Built stays selected after reloading the application');
 await open('Acme invoice September.txt');await check(selected('Original'),'the bank-statement preference leaves invoice naming unchanged');
 await open(names[1]);await nameChoice.getByRole('button',{name:'Original',exact:true}).click();await file();
 await open(names[2]);await check(selected('Original'),'approving Original updates the bank-statement default for the next document');
 await nameChoice.getByRole('button',{name:'Type it',exact:true}).click();await page.locator('#sort-name').fill('Custom statement December');
 await slip.getByText(/After approval,/).waitFor({state:'hidden'});await file();
 await open(names[3]);await check(selected('Original'),'a custom filename keeps the remembered Original default');
 await page.setViewportSize({width:390,height:844});await nameChoice.getByRole('button',{name:'Built',exact:true}).click();
 await slip.getByText(/After approval, Built becomes the default/).waitFor();await check(noOverflow(page),'the naming preference explanation fits a phone');
 await page.waitForTimeout(400); // Let the sidebar finish its viewport transition before capturing.
 await page.screenshot({path:'.local/naming-mode-phone.png',fullPage:true});
 await check(errors.length===0&&!(await page.locator('#blazor-error-ui').isVisible()),'remembered naming choices produce no browser or circuit errors');
 console.log(`All ${checks} naming-mode browser checks passed.`);
}catch(error){await page.screenshot({path:'.local/naming-mode-failure.png',fullPage:true}).catch(()=>{});throw error;}finally{await browser.close();}
