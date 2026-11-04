import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { signIn, ready, nav, connectStorage, enterFolder, newFolder, fileRow, useAsInbox, settleAndRescan, inbox, openInboxItem, toast, noOverflow } from './ui.mjs';

// A protocol fixture, not a language model. Only synthetic test documents belong here.
const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5240';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname), 'Use only a disposable local app');
const directory = path.resolve('.local/ollama-browser-storage');
await mkdir(directory,{recursive:true});
const stamp = Date.now().toString(), guidance = 'Synthetic test guidance '+stamp;
let chatRequests=0, receivedContext=false, requestFailure;
const fixture = http.createServer(async (req,res)=>{
    try {
        res.setHeader('Content-Type','application/json');
        if(req.url==='/api/tags') return res.end(JSON.stringify({models:[{name:'synthetic-reader:latest',size:123},{name:'excluded-cloud',remote_host:'https://example.invalid',size:1}]}));
        let body='';for await(const chunk of req) {body+=chunk;if(body.length>100000)throw Error('Oversized test request');}
        const input=JSON.parse(body);
        if(req.url==='/api/show') {assert.equal(input.model,'synthetic-reader:latest');return res.end(JSON.stringify({capabilities:['completion']}));}
        if(req.url==='/api/chat') {
            chatRequests++;
            assert.equal(input.stream,false);assert.equal(input.format.additionalProperties,false);
            const content=JSON.parse(input.messages[1].content);
            assert(content.document_text.includes('Synthetic Cedar Bank'));
            assert(!JSON.stringify(content.workspace).includes(directory));
            receivedContext=JSON.stringify(content.workspace).includes(guidance);
            const proposal={document_type:'bank_statement',needs_review:false,summary:'Synthetic statement. <img src=x onerror="alert(1)">',facts:[
                {name:'institution',value:'Cedar',evidence:'Cedar Bank'},
                {name:'account_suffix',value:'1234',evidence:'Account 1234'},
                {name:'statement_end',value:'2026-10-31',evidence:'Statement ends 2026-10-31'}
            ]};
            return res.end(JSON.stringify({done:true,message:{role:'assistant',content:JSON.stringify(proposal)}}));
        }
        res.statusCode=404;res.end('{}');
    } catch(error) {requestFailure=error;res.statusCode=500;res.end('{}');}
});
await new Promise(resolve=>fixture.listen(0,'127.0.0.1',resolve));
const endpoint='http://127.0.0.1:'+fixture.address().port;
const credentials=process.env.DOCUMENTSTORAGE_TEST_USERNAME && process.env.DOCUMENTSTORAGE_TEST_PASSWORD
    ? {username:process.env.DOCUMENTSTORAGE_TEST_USERNAME,password:process.env.DOCUMENTSTORAGE_TEST_PASSWORD}
    : JSON.parse(await readFile('.local/browser-credentials.json','utf8'));
const browser=await chromium.launch({executablePath:process.env.DOCUMENTSTORAGE_CHROMIUM || '/usr/bin/chromium',args:['--no-sandbox','--disable-dev-shm-usage']});
const page=await browser.newPage({viewport:{width:1440,height:1050}});
const errors=[];page.on('pageerror',error=>errors.push(error.message));
let checks=0, enabled=false;
async function check(value,label){assert(await value,label);checks++;console.log('PASS '+label);}
const reader=async()=>{await page.goto(base+'/settings?tab=reader');await ready(page);await page.getByLabel('Ollama server address',{exact:true}).waitFor();};
try {
    await page.goto(base);
    await signIn(page,credentials);
    await reader();
    await page.getByLabel('Ollama server address',{exact:true}).fill(endpoint);
    await page.getByRole('button',{name:'Check connection & list models',exact:true}).click();
    await toast(page,'Connected. 1 local model');
    await check(page.locator('#ai-models option').count().then(n=>n===1),'connection check lists installed local models and excludes cloud models');
    await check(chatRequests===0,'connection check sends no document text');
    await page.getByRole('button',{name:'synthetic-reader:latest',exact:true}).click();
    await page.getByLabel('Use the AI reader',{exact:true}).check();
    await page.getByLabel('Read new inbox documents automatically',{exact:true}).uncheck();
    await page.getByRole('button',{name:'Save reader settings',exact:true}).click();
    await toast(page,'AI reader on');enabled=true;
    await nav(page,'Rules');
    await page.locator('.type-item').filter({hasText:'Bank statement'}).click();
    await page.getByRole('button',{name:'Edit type',exact:true}).click();
    await page.getByLabel('How to recognize this type',{exact:true}).fill('A monthly bank account statement. '+guidance);
    await page.getByRole('button',{name:'Save type',exact:true}).click();
    await toast(page,'Document type saved');
    await connectStorage(page,base,'Ollama test archive',directory);
    const incoming='Incoming '+stamp,source='synthetic-'+stamp+'.txt';
    await newFolder(page,incoming);await enterFolder(page,incoming);await useAsInbox(page);
    const content='Synthetic Cedar Bank monthly statement. Account 1234. Statement ends 2026-10-31. Balance 42.50.';
    await page.getByLabel('Upload documents').setInputFiles({name:source,mimeType:'text/plain',buffer:Buffer.from(content)});
    await fileRow(page,source).waitFor();
    await settleAndRescan(page); // Actual production scanner stability window.
    await inbox(page);
    await openInboxItem(page,source);
    await page.getByRole('button',{name:'Read facts only with AI',exact:true}).click();
    await page.getByRole('button',{name:'Use suggestion',exact:true}).waitFor({timeout:20000});
    await check(chatRequests===1 && receivedContext && !requestFailure,'background worker sends synthetic OCR with saved document type guidance through the real Ollama protocol');
    await check(page.locator('.ai-facts blockquote').allTextContents().then(t=>t.some(v=>v.includes('Statement ends 2026-10-31'))),'the suggestion shows supporting evidence from actual indexed OCR');
    await check(page.getByLabel('Document type',{exact:true}).inputValue().then(t=>t===''),'a finished reading leaves the slip untouched until you choose to use it');
    await check(page.locator('.ai-proposal img').count().then(n=>n===0),'model output is escaped instead of being rendered as HTML');
    await check(readFile(path.join(directory,incoming,source),'utf8').then(t=>t===content),'reading leaves the original filename, folder and bytes untouched');
    await page.getByRole('button',{name:'Use suggestion',exact:true}).click();
    await toast(page,'Suggestion copied in');
    await check(page.getByLabel('Document type',{exact:true}).inputValue().then(t=>t==='bank_statement'),'using a suggestion copies its facts into the editable slip');
    await page.getByRole('button',{name:'Save draft',exact:true}).click();
    await toast(page,'Draft saved');
    await page.locator('.intake-item').filter({hasText:source}).locator('.stamp').filter({hasText:'Draft'}).waitFor();
    await check(Promise.resolve(true),'using a suggestion never confirms the facts for you');
    await check(page.getByRole('button',{name:'Use suggestion',exact:true}).count().then(n=>n===0),'saving the slip retires the earlier suggestion');
    await check(readFile(path.join(directory,incoming,source),'utf8').then(t=>t===content),'unconfirmed suggested facts never move the document');
    await page.screenshot({path:'.local/ollama-intake.png',fullPage:true});
    await reader();
    await page.locator('.ai-job').filter({hasText:source}).getByText('Proposed',{exact:true}).waitFor();
    await check(Promise.resolve(true),'the finished reading stays listed with its model');
    await page.setViewportSize({width:390,height:844});
    await check(noOverflow(page),'reader settings, guidance and recent reading fit a mobile viewport');
    await page.screenshot({path:'.local/local-ai-mobile.png',fullPage:true});
    await check(errors.length===0,'the reader workflow has no browser JavaScript errors');
    console.log(`All ${checks} Ollama browser checks passed (protocol fixture, not model accuracy).`);
} finally {
    if(enabled) {
        await page.setViewportSize({width:1440,height:1050});
        await reader();
        await page.getByLabel('Use the AI reader',{exact:true}).uncheck();
        await page.getByRole('button',{name:'Save reader settings',exact:true}).click();
        await toast(page,'AI reader off');
    }
    await browser.close();await new Promise(resolve=>fixture.close(resolve));
}
