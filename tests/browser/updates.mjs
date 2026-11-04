import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { credentials,signIn,ready,noOverflow,chromiumPath } from './ui.mjs';
// Requires the dedicated --updater-browser test executable. It cannot invoke a real Windows task.
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5248';
assert.equal(new URL(base).hostname,'127.0.0.1');
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1440,height:1050}});const errors=[];page.on('pageerror',error=>errors.push(error.message));let count=0;
async function check(value,label){assert(await value,label);count++;console.log('PASS '+label);}
try {
 await page.goto(base);await signIn(page,await credentials());
 const footer=page.locator('.sidebar-version');await check(footer.textContent().then(x=>/v0\.3\.\d+Build \d+ · [a-f0-9]{8}/.test(x)),'menu footer shows the running version, build number and commit');
 await page.locator('.nav-secondary').getByRole('link',{name:'Update',exact:true}).click();await page.getByRole('heading',{name:'Application updates',exact:true}).waitFor();
 await check(page.getByRole('button',{name:'Check for updates',exact:true}).isVisible(),'the menu Update link opens the real Updates settings tab');
 await page.getByRole('button',{name:'Check for updates',exact:true}).click();await ready(page);await page.getByRole('heading',{name:'Available changes',exact:true}).waitFor();
 await check(page.getByText('Synthetic release <script>window.updatePwned=true</script>',{exact:false}).isVisible()&&await page.evaluate(()=>window.updatePwned===undefined),'available commit subjects render as escaped text');
 await check(page.getByText('includes catalog migrations.',{exact:false}).isVisible(),'the release check identifies catalog migrations');
 await page.getByRole('button',{name:'Install update…',exact:true}).click();await page.getByRole('dialog').waitFor();await check(page.getByRole('dialog').getByText(/catalog backup will be taken first/).isVisible(),'install approval explains backup and restart before starting a task');
 await page.getByRole('button',{name:'Cancel',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
 let snapshot=await (await page.request.get(base+'/updates/status')).json();await check(snapshot.status.mode==='check','cancelling approval does not request an installation');
 await page.getByRole('button',{name:'Install update…',exact:true}).click();await page.getByRole('button',{name:'Back up & install',exact:true}).click();await ready(page);
 snapshot=await (await page.request.get(base+'/updates/status')).json();await check(snapshot.status.mode==='update'&&snapshot.status.state==='Succeeded'&&snapshot.status.latest==='b'.repeat(40),'the browser POST requests and displays the exact approved synthetic release');
 await page.screenshot({path:'.local/updater-settings.png',fullPage:true});
 await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'update details fit a mobile viewport');await page.getByRole('button',{name:'Open menu',exact:true}).click();await check(footer.isVisible(),'version and build remain available in the mobile menu');await page.locator('.nav-secondary').getByRole('link',{name:'Update',exact:true}).click();
 // Exercise the actual restart watcher independently from the Blazor circuit using a controlled health response.
 await page.route('**/health',route=>route.fulfill({contentType:'application/json',body:JSON.stringify({status:'ready',revision:'c'.repeat(40)})}));
 const reloaded=page.waitForEvent('framenavigated',frame=>frame===page.mainFrame());
 await page.evaluate(async()=>{const module=await import('/updater.js');const element=document.querySelector('.update-settings');element.dataset.updateActive='true';window.syntheticWatcher=module.watch(element,'a'.repeat(40));});
 await reloaded;await ready(page);await check(new URL(page.url()).searchParams.get('tab')==='updates','the independent restart watcher reloads Updates when a healthy new revision appears');
 await check(errors.length===0,'update checks, approval and reload have no browser script errors');
 console.log(`All ${count} updater browser checks passed (synthetic task; no deployment).`);
}finally{await browser.close();}
