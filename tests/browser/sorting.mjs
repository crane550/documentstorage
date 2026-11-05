// The sorting loop in a real browser: the reader fills in kind, sender, date and tags; the slip shows
// where it will go; Enter files it; a correction is one click; the Filed list undoes; Kinds and
// the Library show what was learned. A loopback stand-in answers for Ollama. Synthetic files only.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, writeFile, readFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, signIn, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, ready, chromiumPath } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5250';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname));
const directory = path.resolve('.local/sorting-browser-' + Date.now());
await mkdir(path.join(directory, 'Inbox'), { recursive: true });
await mkdir(path.join(directory, 'Banking'), { recursive: true });
const statement = 'CEDAR BANK N.A.\nChecking account ending 1234\nStatement period October 1, 2026 to October 31, 2026\nSynthetic closing balance 42.50';
const invoice = 'Acme Supply Co\nINVOICE INV-20391\nDate: 10/14/2026\nSynthetic shop supplies total 84.20';
const recipe = 'Grandma\'s synthetic chili\nTwo cans of beans, one onion, a secret amount of cumin.';
await writeFile(path.join(directory, 'Inbox', '20261105_143022.txt'), statement);
await writeFile(path.join(directory, 'Inbox', 'Acme_Invoice_INV-20391_2026-10-14.txt'), invoice);
await writeFile(path.join(directory, 'Inbox', 'Scan 7.txt'), recipe);

let reads = 0, wireError, sawFolders = false;
const answers = {
    '20261105_143022.txt': { kind: 'Bank statement', kind_description: '', from: 'CEDAR BANK N.A.', date: '2026-10-31', title: 'Checking statement', tags: [], fields: [{ name: 'account_ending', value: '1234', quote: 'ending 1234' }], confidence: 'high' },
    'Acme_Invoice_INV-20391_2026-10-14.txt': { kind: 'Invoice', kind_description: '', from: 'Acme Supply Co', date: '2026-10-14', title: 'Shop supplies', tags: [], fields: [{ name: 'invoice_number', value: 'INV-20391', quote: 'INVOICE INV-20391' }, { name: 'total', value: '84.20', quote: 'total 84.20' }], confidence: 'high' },
    'Scan 7.txt': { kind: 'Recipe', kind_description: 'Cooking recipes.', from: '', date: '', title: 'Chili recipe', tags: ['recipes'], fields: [], confidence: 'medium' }
};
const fixture = http.createServer(async (req, res) => {
    try {
        res.setHeader('Content-Type', 'application/json');
        if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: 'synthetic-sorter:latest', size: 123 }] }));
        let raw = ''; for await (const chunk of req) raw += chunk;
        if (req.url === '/api/show') return res.end('{"capabilities":["completion"]}');
        if (req.url === '/api/chat') {
            const payload = JSON.parse(raw); const input = JSON.parse(payload.messages[1].content);
            assert(payload.think === false && payload.options.num_ctx <= 8192, 'a small, quick request');
            sawFolders ||= raw.includes(directory) || 'folders' in input;
            reads++;
            const answer = answers[input.file_name]; assert(answer, 'a known synthetic file: ' + input.file_name);
            return res.end(JSON.stringify({ done: true, message: { content: JSON.stringify(answer) } }));
        }
        res.statusCode = 404; res.end('{}');
    } catch (e) { wireError = e; res.statusCode = 500; res.end('{}'); }
});
await new Promise(resolve => fixture.listen(0, '127.0.0.1', resolve));
const endpoint = 'http://127.0.0.1:' + fixture.address().port;

const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1100 } });
const errors = []; page.on('pageerror', e => errors.push(e.message));
let checks = 0;
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
const exists = file => access(path.join(directory, file)).then(() => true, () => false);
async function arrives(file) { for (let i = 0; i < 40; i++) { if (await exists(file)) return true; await new Promise(r => setTimeout(r, 250)); } return false; }
const row = name => page.locator('.sort-row').filter({ has: page.locator('strong', { hasText: name }) });
async function open(name) { await row(name).locator('.intake-item').click(); await page.locator('.sort-slip h2').filter({ hasText: name }).waitFor(); }
const shots = path.resolve('.local');

try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Sorting test archive', directory);
    await openFolder(page, 'Inbox'); await useAsInbox(page); await settleAndRescan(page);

    await page.goto(base + '/settings?tab=reader'); await ready(page);
    await page.getByLabel('Ollama server address', { exact: true }).fill(endpoint);
    await page.getByRole('button', { name: 'Check connection & list models', exact: true }).click(); await toast(page, 'Connected.');
    await page.getByRole('button', { name: 'synthetic-sorter:latest', exact: true }).click();
    await page.getByText('Use the AI reader', { exact: true }).click();
    await page.getByText('Read new inbox documents automatically', { exact: true }).click();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');

    for (let i = 0; i < 120 && reads < 3; i++) await new Promise(r => setTimeout(r, 500));
    await page.goto(base + '/'); await ready(page);
    await page.waitForFunction(() => document.querySelectorAll('.sort-row .stamp').length === 3 && [...document.querySelectorAll('.sort-row .stamp')].every(x => !/Queued|Reading|Arriving/.test(x.textContent)), null, { timeout: 60000 });
    await check(reads === 3 && !sawFolders, 'automatic reading asks the narrow question once per document, with no folders or server paths');
    await check(row('20261105_143022.txt').locator('.stamp').textContent().then(t => t.includes('Check')), 'a sender Stash has never seen keeps even a confident reading waiting for a look');
    await check(row('20261105_143022.txt').locator('.sort-goes').textContent().then(t => t.includes('Banking/CEDAR BANK N.A./2026/2026-10 CEDAR BANK N.A. statement 1234.txt') || t.includes('Banking/')), 'each row shows where it will go');

    // A scan: correct the sender with one edit; the path follows before filing; Enter files it.
    await open('20261105_143022.txt');
    await check(page.locator('[data-sort-path]').textContent().then(t => t.includes('Banking') && t.includes('2026')), 'the slip shows the destination, reusing the existing Banking folder');
    await check(page.locator('.path-new').count().then(n => n === 2), 'new folders are marked as new before anything is made');
    await page.getByLabel(/^From/).fill('Cedar Bank'); await page.getByLabel(/^From/).press('Tab');
    await page.waitForFunction(() => document.querySelector('[data-sort-name]')?.textContent.includes('2026-10 Cedar Bank statement'));
    await check(page.locator('[data-sort-name]').textContent().then(t => t.includes('2026-10 Cedar Bank statement 1234.txt')), 'correcting the sender updates the name and folder live');
    await page.screenshot({ path: path.join(shots, 'sorting-slip.png'), fullPage: false });
    await page.locator('.sort-slip h2').click();
    await page.keyboard.press('Enter'); await toast(page, 'Filed as');
    await check(arrives('Banking/Cedar Bank/2026/2026-10 Cedar Bank statement 1234.txt').then(() => readFile(path.join(directory, 'Banking/Cedar Bank/2026/2026-10 Cedar Bank statement 1234.txt'), 'utf8')).then(x => x === statement), 'Enter files it: folders made, renamed, bytes unchanged');
    await check(exists('Inbox/20261105_143022.txt').then(x => !x), 'the scan has left the inbox');

    // An emailed document keeps its descriptive name; a tag is added by typing and Enter.
    await open('Acme_Invoice_INV-20391_2026-10-14.txt');
    await check(page.getByLabel('Keep the original name', { exact: true }).isChecked(), 'a descriptive emailed name is kept by default');
    await page.getByLabel('Add a tag', { exact: true }).fill('shop'); await page.getByLabel('Add a tag', { exact: true }).press('Enter');
    await page.locator('.tag-chip').filter({ hasText: '#shop' }).waitFor();
    await check(exists('Inbox/Acme_Invoice_INV-20391_2026-10-14.txt'), 'Enter in the tag box adds a tag without filing');
    await page.getByRole('button', { name: /Approve & file/ }).click(); await toast(page, 'Filed as');
    await check(arrives('Invoices/Acme Supply Co/2026/Acme_Invoice_INV-20391_2026-10-14.txt'), 'the invoice files under its kind and sender with its own name');

    // Something new: the reader proposes a new kind; approving makes it.
    await open('Scan 7.txt');
    await check(page.locator('.new-mark').first().isVisible(), 'a kind the reader made up is marked new');
    await page.getByRole('button', { name: /Approve & file/ }).click(); await toast(page, 'Filed as');
    await check(arrives('Recipe/Chili recipe.txt'), 'a new kind files under its own name until its patterns are set');
    await page.locator('.caught-up').waitFor();

    // Filed list, and Undo.
    await page.getByRole('button', { name: 'Filed', exact: true }).click();
    await check(page.waitForFunction(() => document.querySelectorAll('.filed-row').length === 3, null, { timeout: 10000 }).then(() => true), 'the Filed list shows the three filings');
    await check(page.locator('.filed-row').filter({ hasText: '2026-10 Cedar Bank statement 1234.txt' }).locator('.stamp').textContent().then(t => t.includes('Corrected')), 'a corrected filing is marked as such');
    await page.locator('.filed-row').filter({ hasText: 'Chili recipe.txt' }).getByRole('button', { name: 'Undo' }).click(); await toast(page, 'back in the inbox');
    await check(arrives('Inbox/Scan 7.txt'), 'undo puts the document back in the inbox');
    await page.getByRole('button', { name: /Waiting/ }).click();
    await open('Scan 7.txt');
    await check(page.getByLabel(/^Kind/).inputValue().then(x => x === 'Recipe'), 'an undone document comes back filled in');

    // Kinds: the new kind exists; switching a kind to file itself.
    await page.goto(base + '/kinds'); await ready(page);
    await check(page.locator('.kind-card h2').filter({ hasText: /^Recipe$/ }).count().then(n => n === 1), 'the new kind is on the Kinds page');
    const statements = page.locator('.kind-card').filter({ has: page.locator('h2', { hasText: /^Bank statement$/ }) });
    await check(statements.locator('.kind-record').textContent().then(t => t.includes('1 approved') && t.includes('1 corrected')), 'each kind shows its record of approvals and corrections');
    await statements.getByText('File without asking').click(); await toast(page, 'now file themselves');
    await page.getByRole('button', { name: /Senders/ }).click();
    await check(page.locator('.name-row').filter({ hasText: 'also CEDAR BANK N.A' }).waitFor({ timeout: 10000 }).then(() => true), 'the reader\'s spelling is remembered for the sender');
    await page.screenshot({ path: path.join(shots, 'sorting-kinds.png'), fullPage: true });

    // Library: details and search by them.
    await page.goto(base + '/library?q=' + encodeURIComponent('tag:shop')); await ready(page);
    await page.locator('.file-name').filter({ hasText: 'Acme_Invoice' }).first().click();
    await page.locator('.details-card').filter({ hasText: 'Acme Supply Co' }).waitFor();
    await check(page.locator('.details-card').textContent().then(t => t.includes('Invoice') && t.includes('#shop')), 'the Library shows a document\'s details and finds it by tag');

    // Phone width.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(base + '/'); await ready(page); await open('Scan 7.txt');
    await check(page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'the inbox fits a phone without sideways scrolling');
    await page.screenshot({ path: path.join(shots, 'sorting-phone.png'), fullPage: true });
    await check(errors.length === 0 && !wireError, 'no page errors and no unexpected model requests');
    console.log(`\n${checks} sorting browser checks passed.`);
} catch (error) {
    await page.screenshot({ path: path.join(shots, 'sorting-failure.png'), fullPage: true }).catch(() => { });
    console.error(errors, wireError); throw error;
} finally {
    await browser.close(); fixture.close();
}
