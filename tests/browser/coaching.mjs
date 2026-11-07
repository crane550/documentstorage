import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { credentials, chromiumPath, signIn, ready, toast, noOverflow } from './ui.mjs';
const base=process.env.DOCUMENTSTORAGE_TEST_URL||'http://127.0.0.1:5252';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Disposable local app only');
const browser=await chromium.launch({executablePath:chromiumPath(),args:['--no-sandbox','--disable-dev-shm-usage']});const page=await browser.newPage({viewport:{width:1440,height:1050}});
const errors=[];page.on('pageerror',e=>errors.push(e.message));let checks=0,token,rpcId=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
async function rpc(method,params){
 const r=await fetch(base+'/mcp',{method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json',Accept:'application/json, text/event-stream','MCP-Protocol-Version':'2025-11-25'},body:JSON.stringify({jsonrpc:'2.0',id:++rpcId,method,params})});const raw=await r.text();assert.equal(r.status,200);
 return JSON.parse(raw.trim().startsWith('{')?raw:raw.split('\n').find(x=>x.startsWith('data: ')).slice(6));
}
async function tool(name,args={}){const r=await rpc('tools/call',{name,arguments:args});assert(!r.error&&!r.result.isError,'MCP call '+name+' succeeds');return r.result.structuredContent.data;}
async function settings(){await page.goto(base+'/settings?tab=assistants');await ready(page);}
try{
 await page.goto(base);await signIn(page,await credentials());await settings();
 const stamp=Date.now(),name='Coaching browser '+stamp,redirect='https://client.example.test/coaching-callback';
 await page.getByLabel('Connection name',{exact:true}).fill(name);await page.getByLabel('OAuth callback URLs',{exact:true}).fill(redirect);
 await page.getByLabel('Apply document corrections and reader training directly (no file moves or automatic-filing trust changes)',{exact:true}).check();
 await page.getByRole('button',{name:'Create MCP connection',exact:true}).click();
 const clientId=await page.getByLabel('OAuth client ID',{exact:true}).inputValue(),secret=await page.getByLabel('OAuth client secret · shown once',{exact:true}).inputValue();
 await page.getByRole('button',{name:'Hide connection details',exact:true}).click();
 await page.getByLabel('Change handling for '+name,{exact:true}).selectOption('apply');await toast(page,'Assistant change handling updated.');
 const verifier=crypto.randomBytes(32).toString('base64url'),challenge=crypto.createHash('sha256').update(verifier).digest('base64url');
 const auth=new URL(base+'/connect/authorize');for(const [k,v]of Object.entries({client_id:clientId,redirect_uri:redirect,response_type:'code',scope:'catalog.read documents.read sorting.manage',resource:base+'/mcp',state:'coaching-state',code_challenge:challenge,code_challenge_method:'S256'}))auth.searchParams.set(k,v);
 let callback;
 await page.route(base+'/connect/authorize**',async route=>{if(route.request().method()!=='POST')return route.continue();const r=await route.fetch({maxRedirects:0});callback=new URL(r.headers().location,base);await route.fulfill({status:200,contentType:'text/html',body:'<title>Coaching callback</title>'});});
 await page.goto(auth.toString());await page.getByRole('button',{name:'Allow connection',exact:true}).click();await page.waitForFunction(()=>document.title==='Coaching callback');
 const exchange=await fetch(base+'/connect/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'authorization_code',client_id:clientId,client_secret:secret,redirect_uri:redirect,code:callback.searchParams.get('code'),code_verifier:verifier,resource:base+'/mcp'})});assert(exchange.ok);token=(await exchange.json()).access_token;
 await rpc('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'Synthetic coaching browser',version:'1'}});
 const context=await tool('get_context');await check(Promise.resolve(context.assistant_changes.mode==='apply_then_verify'&&context.assistant_changes.can_edit),'connection settings and actual OAuth grants expose apply-then-verify mode');
 const applied=await tool('update_tag',{tag_id:null,expected_revision:0,name:'coached-'+stamp,aliases:[],source_request:'Create a reusable synthetic safety tag.',context_hash:context.context_hash,idempotency_key:'browser-applied-'+stamp});
 await check(Promise.resolve(applied.applied),'an authorized stronger assistant applies a change directly');
 await page.goto(base);await ready(page);await page.locator('.assistant-changes > summary').click();
 const card=page.locator('.assistant-change[data-change-id="'+applied.receipt_id+'"]');await card.getByRole('button',{name:'Mark verified',exact:true}).click();await toast(page,'Assistant change verified.');
 await check((await tool('get_assistant_edit',{receipt_id:applied.receipt_id})).state==='Verified','the Inbox verifies a change after it has already been applied');
 await settings();await page.getByLabel('Change handling for '+name,{exact:true}).selectOption('review');await toast(page,'Assistant change handling updated.');
 const pending=await tool('update_tag',{tag_id:null,expected_revision:0,name:'reviewed-'+stamp,aliases:[],source_request:'Review this synthetic tag before applying.',context_hash:(await tool('get_context')).context_hash,idempotency_key:'browser-pending-'+stamp});
 await check(Promise.resolve(!pending.applied&&pending.state==='Pending'),'switching back to review-first works with the existing OAuth connection');
 await page.goto(base);await ready(page);await page.locator('.assistant-changes > summary').click();
 await page.locator('.assistant-change[data-change-id="'+pending.receipt_id+'"]').getByRole('button',{name:'Apply change',exact:true}).click();await toast(page,'Assistant change applied.');
 await check((await tool('get_assistant_edit',{receipt_id:pending.receipt_id})).state==='Applied','a review-first change applies through the Inbox');
 const override=await tool('update_tag',{tag_id:null,expected_revision:0,name:'go-ahead-'+stamp,aliases:[],apply_now:true,source_request:'Go ahead and apply this synthetic tag now.',context_hash:(await tool('get_context')).context_hash,idempotency_key:'browser-override-'+stamp});
 await check(Promise.resolve(override.applied),'an explicit go-ahead overrides the default review mode');
 await page.setViewportSize({width:390,height:844});await check(noOverflow(page),'verification controls fit a phone viewport');await check(errors.length===0,'coaching settings and verification produce no browser errors');
 console.log(`All ${checks} coaching browser checks passed.`);
}finally{await browser.close();}
