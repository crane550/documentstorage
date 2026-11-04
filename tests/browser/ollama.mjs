import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';

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
const localAi=()=>page.locator('.main-nav').getByRole('button',{name:'Local AI',exact:true}).click();
try {
    await page.goto(base);
    await page.getByLabel('Username',{exact:true}).fill(credentials.username);
    await page.getByLabel('Password',{exact:true}).fill(credentials.password);
    await page.getByRole('button',{name:'Sign in',exact:true}).click();
    await page.waitForFunction(()=>{const b=document.querySelector('.heading-actions .primary');return b&&!b.disabled;});
    await localAi();
    await page.getByLabel('Ollama server address',{exact:true}).fill(endpoint);
    await page.getByRole('button',{name:'Check connection & list models',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Connected. 1 local model'}).waitFor();
    await check(page.locator('#ai-models option').count().then(n=>n===1),'connection check lists installed local models and excludes cloud models');
    await check(chatRequests===0,'connection check sends no document text');
    await page.getByRole('button',{name:'synthetic-reader:latest',exact:true}).click();
    await page.getByLabel('Enable local document identification',{exact:true}).check();
    await page.getByLabel('Automatically identify new intake documents',{exact:true}).uncheck();
    await page.getByRole('button',{name:'Save local AI settings',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Local identification enabled'}).waitFor();enabled=true;
    await page.getByLabel('Document type to edit',{exact:true}).selectOption('bank_statement');
    await page.getByLabel('How to recognize this type',{exact:true}).fill('A monthly bank account statement. '+guidance);
    await page.getByRole('button',{name:'Save document guidance',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Document guidance saved'}).waitFor();
    await page.locator('.main-nav').getByRole('button',{name:/^Documents/}).click();
    const tree=page.locator('.tree-label').filter({hasText:'Ollama test archive'});
    if(await tree.count()===0) {
        await page.getByRole('button',{name:'Add storage',exact:true}).click();
        await page.getByLabel('Location name').fill('Ollama test archive');
        await page.getByLabel('Full path on the server').fill(directory);
        await page.getByRole('button',{name:'Connect storage',exact:true}).click();
        await page.getByRole('dialog').waitFor({state:'hidden'});
    } else {await tree.click();await page.locator('.breadcrumbs').filter({hasText:'Ollama test archive'}).waitFor();}
    const incoming='Incoming '+stamp,source='synthetic-'+stamp+'.txt';
    await page.getByRole('button',{name:'New folder',exact:true}).click();
    await page.getByLabel('Name',{exact:true}).fill(incoming);
    await page.getByRole('button',{name:'Create folder',exact:true}).click();
    await page.getByRole('dialog').waitFor({state:'hidden'});
    await page.locator('.file-name').filter({hasText:incoming}).click();
    await page.getByRole('button',{name:'Use as intake',exact:true}).click();
    await page.getByRole('button',{name:'Stop using as intake',exact:true}).waitFor();
    const content='Synthetic Cedar Bank monthly statement. Account 1234. Statement ends 2026-10-31. Balance 42.50.';
    await page.getByLabel('Upload documents').setInputFiles({name:source,mimeType:'text/plain',buffer:Buffer.from(content)});
    await page.locator('.file-name').filter({hasText:source}).waitFor();
    await new Promise(resolve=>setTimeout(resolve,5500)); // Actual production scanner stability window.
    await page.getByRole('button',{name:'Rescan storage',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Storage checked.'}).waitFor();
    await page.locator('.main-nav').getByRole('button',{name:/^Intake/}).click();
    await page.locator('.intake-item').filter({hasText:source}).click();
    await page.getByRole('button',{name:'Identify with Ollama',exact:true}).click();
    await page.getByRole('button',{name:'Use proposed facts',exact:true}).waitFor({timeout:20000});
    await check(chatRequests===1 && receivedContext && !requestFailure,'background worker sends synthetic OCR with saved document type guidance through the real Ollama protocol');
    await check(page.locator('.ai-facts blockquote').allTextContents().then(t=>t.some(v=>v.includes('Statement ends 2026-10-31'))),'proposal shows supporting evidence from actual indexed OCR');
    await check(page.getByLabel('Document type',{exact:true}).inputValue().then(t=>t===''),'model completion leaves the manual review editor untouched');
    await check(page.locator('.ai-proposal img').count().then(n=>n===0),'model output is escaped instead of being rendered as HTML');
    await check(readFile(path.join(directory,incoming,source),'utf8').then(t=>t===content),'identification leaves the original filename, folder and bytes untouched');
    await page.getByRole('button',{name:'Use proposed facts',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Proposed facts copied'}).waitFor();
    await check(page.getByLabel('Document type',{exact:true}).inputValue().then(t=>t==='bank_statement'),'explicit acceptance copies proposed facts into the editable review');
    await check(page.getByLabel('I checked these facts against the document').isChecked().then(x=>!x),'accepting model facts never checks the human review confirmation');
    await page.getByRole('button',{name:'Save & preview',exact:true}).click();
    await page.locator('.filing-preview.blocked').waitFor();
    await check(page.getByRole('button',{name:'Use proposed facts',exact:true}).count().then(n=>n===0),'saving a review removes acceptance of the earlier stale proposal');
    await check(page.getByRole('button',{name:'Rename & file',exact:true}).count().then(n=>n===0),'unconfirmed model facts cannot produce an executable filing preview');
    await page.screenshot({path:'.local/ollama-intake.png',fullPage:true});
    await localAi();
    await page.locator('.ai-job').filter({hasText:source}).getByText('Proposed',{exact:true}).waitFor();
    await check(Promise.resolve(true),'completed job remains visible with its model and attempt history');
    await page.setViewportSize({width:390,height:844});
    await check(page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth),'local AI settings, guidance and job history fit a mobile viewport');
    await page.screenshot({path:'.local/local-ai-mobile.png',fullPage:true});
    await check(errors.length===0,'Ollama workflow has no browser JavaScript errors');
    console.log(`All ${checks} Ollama browser checks passed (protocol fixture, not model accuracy).`);
} finally {
    if(enabled) {
        await localAi();
        await page.getByLabel('Enable local document identification',{exact:true}).uncheck();
        await page.getByRole('button',{name:'Save local AI settings',exact:true}).click();
        await page.getByRole('status').filter({hasText:'Local identification is off'}).waitFor();
    }
    await browser.close();await new Promise(resolve=>fixture.close(resolve));
}
