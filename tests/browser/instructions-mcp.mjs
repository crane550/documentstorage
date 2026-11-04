import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import crypto from 'node:crypto';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const base=process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5240';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname),'Use only a disposable local catalog');
const directory=path.resolve('.local/assistant-browser-storage');await mkdir(directory,{recursive:true});
const stamp=Date.now().toString();
const guide='Identify monthly bank statements.\nUse the statement closing date.\nExtract only the last four account digits.';
let modelCalls=0;
const fixture=http.createServer(async(req,res)=>{
    res.setHeader('Content-Type','application/json');
    if(req.url==='/api/tags')return res.end(JSON.stringify({models:[{name:'synthetic-guide:latest',size:123}]}));
    let raw='';for await(const chunk of req)raw+=chunk;const payload=JSON.parse(raw);
    if(req.url==='/api/show')return res.end(JSON.stringify({capabilities:['completion']}));
    if(req.url==='/api/chat'){
        modelCalls++;const input=JSON.parse(payload.messages[1].content);
        assert(payload.format.required.includes('guidance'));
        const clarify=input.source_conversation.at(-1).Text.includes('missing closing date');
        const content={reply:clarify?'Which date should be used when the closing date is missing?':'Here is a concise draft. <img src=x onerror="alert(1)">',guidance:clarify?'This must never be published.':guide,needs_clarification:clarify};
        return res.end(JSON.stringify({done:true,message:{content:JSON.stringify(content)}}));
    }
    res.statusCode=404;res.end('{}');
});
await new Promise(resolve=>fixture.listen(0,'127.0.0.1',resolve));
const endpoint='http://127.0.0.1:'+fixture.address().port;
const credentials=process.env.DOCUMENTSTORAGE_TEST_USERNAME && process.env.DOCUMENTSTORAGE_TEST_PASSWORD
    ? {username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}
    : JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM || '/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1440,height:1050}});
const errors=[];page.on('pageerror',error=>errors.push(error.message));let enabled=false,checks=0;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const nav=name=>page.locator('.main-nav').getByRole('button',{name,exact:true}).click();
let token,clientId,secret,rpcId=0;
async function protocol(method,params){
    const response=await fetch(base+'/mcp',{method:'POST',headers:{Authorization:'Bearer '+token,'Content-Type':'application/json',Accept:'application/json, text/event-stream','MCP-Protocol-Version':'2025-11-25'},body:JSON.stringify({jsonrpc:'2.0',id:++rpcId,method,params})});
    const raw=await response.text();
    if(!response.ok)return {status:response.status};
    const data=raw.trim().startsWith('{')?raw:raw.split('\n').find(x=>x.startsWith('data: '))?.slice(6);
    return {...JSON.parse(data),status:response.status};
}
async function tool(name,args={}){
    const response=await protocol('tools/call',{name,arguments:args});
    assert.equal(response.status,200,'MCP tool HTTP response');assert(!response.error,'MCP protocol error');
    assert(response.result.isError!==true,'MCP tool validation failed');return response.result.structuredContent.data;
}
try{
    await page.goto(base);
    await page.getByLabel('Username',{exact:true}).fill(credentials.username);await page.getByLabel('Password',{exact:true}).fill(credentials.password);
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});
    const tree=page.locator('.tree-label').filter({hasText:'Assistant browser archive'});
    if(await tree.count()===0){
        await page.getByRole('button',{name:'Add storage',exact:true}).click();await page.getByLabel('Location name').fill('Assistant browser archive');await page.getByLabel('Full path on the server').fill(directory);
        await page.getByRole('button',{name:'Connect storage',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
    }else{await tree.click();await page.locator('.breadcrumbs').filter({hasText:'Assistant browser archive'}).waitFor();}
    const destination='Filed '+stamp;
    await page.getByRole('button',{name:'New folder',exact:true}).click();await page.getByLabel('Name',{exact:true}).fill(destination);
    await page.getByRole('button',{name:'Create folder',exact:true}).click();await page.getByRole('dialog').waitFor({state:'hidden'});
    await nav('Local AI');
    await page.getByLabel('Ollama server address',{exact:true}).fill(endpoint);await page.getByLabel('Installed model',{exact:true}).fill('synthetic-guide:latest');
    await page.getByLabel('Enable local document identification',{exact:true}).check();await page.getByLabel('Automatically identify new intake documents',{exact:true}).uncheck();
    await page.getByRole('button',{name:'Save local AI settings',exact:true}).click();await page.getByRole('status').filter({hasText:'Local identification enabled'}).waitFor();enabled=true;
    await nav('Instructions');await page.getByLabel('Instructions for',{exact:true}).waitFor();
    const before=await page.locator('.instruction-canonical').first().textContent();
    await page.getByLabel('Your instruction',{exact:true}).fill('These are monthly bank statements. Use the closing date and keep only the last four account digits.');
    await page.getByRole('button',{name:'Send instruction',exact:true}).click();
    await page.getByRole('button',{name:'Save this version',exact:true}).waitFor();
    await check(page.locator('.instruction-canonical').first().textContent().then(x=>x===before),'conversational drafting preserves canonical guidance until acceptance');
    await check((await page.locator('.instruction-message').count())>=2&&modelCalls===1,'source request and structured local assistant reply appear in the conversation');
    await check(page.locator('.instruction-message img').count().then(x=>x===0),'assistant conversation content is escaped as text');
    await page.getByRole('button',{name:'Save this version',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Instruction version saved with its source conversation'}).waitFor();
    await check(page.locator('.instruction-canonical').first().textContent().then(x=>x===guide),'accepting a conversational draft publishes the exact proposed guidance');
    await page.getByLabel('Your instruction',{exact:true}).fill('What should happen with a missing closing date?');
    await page.getByRole('button',{name:'Send instruction',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Which date should be used'}).waitFor();
    await check((await page.getByRole('button',{name:'Save this version',exact:true}).count())===0&&(await page.locator('.instruction-canonical').first().textContent())===guide,'a clarification question never publishes the model\'s replacement text');
    await page.getByText('Edit a draft yourself',{exact:true}).click();
    const manual=guide+'\nFlag missing closing dates for review.';
    await page.getByLabel('Replacement instructions',{exact:true}).fill(manual);await page.getByRole('button',{name:'Review this version',exact:true}).click();
    await page.getByRole('button',{name:'Save this version',exact:true}).click();await page.getByRole('status').filter({hasText:'Instruction version saved'}).waitFor();
    await nav('Assistants & MCP');await nav('Instructions');
    await check((await page.locator('.instruction-canonical').first().textContent())===manual&&(await page.locator('.instruction-message').count())>=4,'conversation and accepted instruction versions survive navigation and reload');
    await nav('Assistants & MCP');
    const connectionName='Browser assistant '+stamp+' <img src=x>';
    await page.getByLabel('Connection name',{exact:true}).fill(connectionName);
    const redirect='https://client.example.test/browser-callback';await page.getByLabel('OAuth callback URLs',{exact:true}).fill(redirect);
    await page.getByLabel('Read indexed document text and reviewed facts',{exact:true}).check();
    await page.getByRole('button',{name:'Create MCP connection',exact:true}).click();
    await page.getByLabel('OAuth client ID',{exact:true}).waitFor();clientId=await page.getByLabel('OAuth client ID',{exact:true}).inputValue();secret=await page.getByLabel('OAuth client secret · shown once',{exact:true}).inputValue();
    await check(clientId.startsWith('ds_')&&secret.length>40,'connection management issues a client ID and one-time secret through the real UI');
    await page.getByRole('button',{name:'Hide connection details',exact:true}).click();
    await page.getByLabel('OAuth client secret · shown once',{exact:true}).waitFor({state:'hidden'});
    await check(page.getByLabel('OAuth client secret · shown once',{exact:true}).count().then(x=>x===0),'hiding connection details removes the secret from the rendered page');
    const verifier=crypto.randomBytes(32).toString('base64url'),challenge=crypto.createHash('sha256').update(verifier).digest('base64url');
    const auth=new URL(base+'/connect/authorize');for(const [key,value]of Object.entries({client_id:clientId,redirect_uri:redirect,response_type:'code',scope:'catalog.read documents.read instructions.propose rules.propose offline_access',resource:base+'/mcp',state:'browser-state',code_challenge:challenge,code_challenge_method:'S256'}))auth.searchParams.set(key,value);
    let callback,callbackStatus;
    // Capture the real consent redirect without navigating to an external test hostname.
    await page.route(base+'/connect/authorize**',async route=>{
        if(route.request().method()!=='POST')return route.continue();
        const response=await route.fetch({maxRedirects:0});callbackStatus=response.status();
        const location=response.headers().location;if(location)callback=new URL(location,base);
        await route.fulfill({status:200,contentType:'text/html',body:'<!doctype html><title>Synthetic callback</title>Consent response captured.'});
    });
    await page.goto(auth.toString());await page.getByRole('button',{name:'Allow connection',exact:true}).waitFor();
    await check(page.locator('img').count().then(x=>x===0),'OAuth consent escapes the registered client name');
    await page.getByRole('button',{name:'Allow connection',exact:true}).click();await page.waitForFunction(()=>document.title==='Synthetic callback');
    assert.equal(callbackStatus,302,'Browser consent must issue a redirect');assert.equal(callback.origin,'https://client.example.test');assert(callback.searchParams.has('code'),'Browser consent must issue an authorization code');
    assert.equal(callback.searchParams.get('state'),'browser-state');
    const exchanged=await fetch(base+'/connect/token',{method:'POST',headers:{'Content-Type':'application/x-www-form-urlencoded'},body:new URLSearchParams({grant_type:'authorization_code',client_id:clientId,client_secret:secret,redirect_uri:redirect,code:callback.searchParams.get('code'),code_verifier:verifier,resource:base+'/mcp'})});
    assert(exchanged.ok,'Browser OAuth code exchange');token=(await exchanged.json()).access_token;secret=null;
    const initialized=await protocol('initialize',{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'Synthetic browser MCP client',version:'1'}});
    await check(initialized.status===200,'browser-issued OAuth credentials initialize a real MCP connection');
    const discovered=await protocol('tools/list',{});
    await check(discovered.result.tools.every(x=>x.outputSchema?.properties?.ok?.type==='boolean'),'MCP tool discovery includes the versioned result-envelope schema');
    await check(!discovered.result.tools.some(x=>x.name==='execute_filing'),'MCP hides filing execution when the connection was not granted that permission');
    const context=await tool('get_context'),instructions=await tool('get_instructions',{kind:'global',key:'global'});
    const source=await tool('add_instruction_message',{kind:'global',key:'global',text:'Never infer account numbers that are absent from a statement.',idempotency_key:'browser-source-'+stamp});
    const proposalArguments={kind:'global',key:'global',base_hash:instructions.hash,text:manual+'\nNever infer absent account numbers.',explanation:'Synthetic external assistant instruction update.',source_messages:[source.message_id],context_hash:context.context_hash,idempotency_key:'browser-guidance-'+stamp};
    const proposal=await tool('propose_instructions',proposalArguments);
    await page.goto(base);await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});await nav('Instructions');
    await check(page.locator('.instruction-canonical').first().textContent().then(x=>x===manual),'an external MCP instruction proposal waits for browser acceptance');
    await nav('Assistants & MCP');await page.locator('.rule-list-item').filter({hasText:connectionName}).filter({hasText:'Instruction update'}).click();
    await page.getByRole('button',{name:'Save instruction version',exact:true}).click();await page.getByRole('status').filter({hasText:'Proposed change saved'}).waitFor();
    await check((await tool('get_proposal',{proposal_id:proposal.id})).state==='Applied','browser acceptance is visible as a completed result to the MCP client');
    await check((await tool('propose_instructions',proposalArguments)).id===proposal.id,'replaying an accepted MCP proposal returns the original result after context changes');
    const storage=(await tool('list_storage')).items.find(x=>x.name==='Assistant browser archive');
    const folder=(await tool('list_folders',{storage_id:storage.id})).items.find(x=>x.name===destination);
    const ruleName='Browser proposed rule '+stamp;
    const rule=await tool('propose_rule',{input:{id:null,expected_revision:0,name:ruleName,document_type:'browser_statement_'+stamp,destination_folder_id:folder.id,match:{},filename_template:'{document_date:yyyy-MM-dd}.{extension}',required_facts:['document_date'],enabled:false},source_request:'File these synthetic statements by document date in the selected folder.',context_hash:(await tool('get_context')).context_hash,idempotency_key:'browser-rule-'+stamp});
    await page.getByRole('button',{name:'Refresh assistant activity',exact:true}).click();await page.locator('.rule-list-item').filter({hasText:ruleName}).click();
    await page.getByRole('button',{name:'Save paused rule',exact:true}).click();await page.getByRole('status').filter({hasText:'Proposed change saved'}).waitFor();
    await nav('Filing rules');await page.locator('.rule-list-item').filter({hasText:ruleName}).getByText('Paused',{exact:true}).waitFor();
    await check((await tool('get_proposal',{proposal_id:rule.id})).state==='Applied','an MCP rule proposal becomes a paused versioned rule only after browser acceptance');
    await nav('Assistants & MCP');await page.locator('.connection-row').filter({hasText:connectionName}).getByRole('button',{name:'Revoke connection',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Connection revoked'}).waitFor();
    const revoked=await protocol('tools/list',{});await check([401,403].includes(revoked.status),'browser revocation immediately blocks the issued MCP credentials');
    await page.setViewportSize({width:390,height:844});await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'connection management and proposal review fit a mobile viewport');
    await page.screenshot({path:'.local/assistant-connections.png',fullPage:true});
    await nav('Instructions');await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'instruction conversations and version history fit a mobile viewport');
    await page.screenshot({path:'.local/instruction-conversation.png',fullPage:true});
    await check(errors.length===0,'instruction and MCP workflows have no browser JavaScript errors');
    console.log(`All ${checks} instruction and MCP browser checks passed (synthetic model fixture).`);
}finally{
    try{
        if(clientId){await page.goto(base);await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});await nav('Assistants & MCP');const revoke=page.locator('.connection-row').filter({hasText:clientId}).getByRole('button',{name:'Revoke connection',exact:true});if(await revoke.count()){await revoke.click();await page.getByRole('status').filter({hasText:'Connection revoked'}).waitFor();}}
        if(enabled){await page.goto(base);await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});await nav('Local AI');await page.getByLabel('Enable local document identification',{exact:true}).uncheck();await page.getByRole('button',{name:'Save local AI settings',exact:true}).click();await page.getByRole('status').filter({hasText:'Local identification is off'}).waitFor();}}finally{await browser.close();await new Promise(resolve=>fixture.close(resolve));}
}
