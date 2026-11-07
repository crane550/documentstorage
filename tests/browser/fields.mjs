// Fields in a real browser: the reader reads a statement without its fields; Find with AI fills the
// empty one; Set up fields with AI suggests the kind's fields with this document's values, which are
// kept; a field is described by hand; and the next statement's fields come from their printed words
// with no reader asked. Synthetic statements and a loopback stand-in for Ollama only.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, signIn, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, ready, chromiumPath, noOverflow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5250';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname));
const directory = path.resolve('.local/fields-browser-' + Date.now());
await mkdir(path.join(directory, 'Inbox'), { recursive: true });
const statement = (m, begin, end) => `SYNTHETIC CEDAR BANK\nAccount Number: ****5678\nStatement period ${m}/1/2026 to ${m}/28/2026\nBeginning balance    $${begin}\nEnding Balance       $${end}\nThank you for banking with us.`;
const name = m => `scan-0${m}.txt`;

const asked = { sort: 0, find: [], draft: 0 };
let wireError;
const fixture = http.createServer(async (req, res) => {
    try {
        res.setHeader('Content-Type', 'application/json');
        if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: 'synthetic-sorter:latest', size: 123 }] }));
        let raw = ''; for await (const chunk of req) raw += chunk;
        if (req.url === '/api/show') return res.end('{"capabilities":["completion"]}');
        if (req.url === '/api/chat') {
            const body = JSON.parse(raw), system = body.messages[0].content, input = JSON.parse(body.messages[1].content);
            let answer;
            if (system.startsWith('You sort')) {
                asked.sort++;
                const m = Number(input.file_name.slice(6, 7));
                answer = { kind: 'Bank statement', kind_description: '', from: 'Synthetic Cedar Bank', date: `2026-0${m}-28`, title: 'Checking statement', tags: [], fields: [], confidence: 'medium' };
            } else if (system.startsWith('You find values')) {
                asked.find.push(input.fields.map(x => x.name));
                answer = { fields: [{ name: 'account_ending', value: '5678', quote: 'Account Number: ****5678' }] };
            } else if (system.startsWith('You set up')) {
                asked.draft++;
                answer = { description: 'Monthly statements for checking and savings accounts.', fields: [
                    { name: 'Account ending', type: 'id', look_for: 'The last four digits of the account number.', labels: ['Account Number'], value: '5678', quote: '****5678' },
                    { name: 'Beginning balance', type: 'money', look_for: 'The balance at the start of the statement period.', labels: ['Beginning balance', 'Previous balance'], value: '$1,204.33', quote: 'Beginning balance    $1,204.33' },
                    { name: 'Ending balance', type: 'money', look_for: 'The balance at the end of the statement period.', labels: [], value: '980.10', quote: 'Ending Balance $980.10' },
                    { name: 'Routing number', type: 'id', look_for: "The bank's routing number.", labels: ['Routing'], value: '', quote: '' }
                ] };
            } else throw new Error('unexpected question');
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
const all = (...values) => Promise.all(values).then(v => v.every(Boolean));
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
const shots = path.resolve('.local');
const field = key => page.locator(`#sort-field-${key}`);
const inInbox = m => access(path.join(directory, 'Inbox', name(m))).then(() => true, () => false);
async function gone(m) { for (let i = 0; i < 240; i++) { if (!await inInbox(m)) return true; await new Promise(r => setTimeout(r, 250)); } return false; }
async function arrive(m, begin, end) {
    await writeFile(path.join(directory, 'Inbox', name(m)), statement(m, begin, end));
    await page.goto(base + '/library'); await ready(page); await settleAndRescan(page); await settleAndRescan(page);
    for (let i = 0; i < 120 && asked.sort < m; i++) await new Promise(r => setTimeout(r, 500));
    await page.goto(base + '/'); await ready(page);
    await page.locator('.sort-row').filter({ hasText: name(m) }).locator('.stamp').filter({ hasNotText: /Queued|Reading/ }).waitFor();
    await page.locator('.sort-row').filter({ has: page.locator('strong', { hasText: name(m) }) }).locator('.intake-item').click();
    await page.locator('.sort-slip h2').filter({ hasText: name(m) }).waitFor();
}

try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Fields test archive', directory);
    await openFolder(page, 'Inbox'); await useAsInbox(page);
    await page.goto(base + '/settings?tab=reader'); await ready(page);
    await page.getByLabel('Ollama server address', { exact: true }).fill(endpoint);
    await page.getByRole('button', { name: 'Check connection & list models', exact: true }).click(); await toast(page, 'Connected.');
    await page.getByRole('button', { name: 'synthetic-sorter:latest', exact: true }).click();
    await page.getByText('Use the AI reader', { exact: true }).click();
    await page.getByText('Read new inbox documents automatically', { exact: true }).click();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');

    // The first statement: read, but its kind's one field is empty.
    await arrive(1, '1,204.33', '980.10');
    await check(field('account_ending').inputValue().then(v => v === ''), 'the reading leaves the account ending empty');
    await check(page.locator('[data-find-fields]').textContent().then(t => t.includes('Find 1 empty with AI')), 'the slip offers to find the empty field');
    await page.locator('[data-find-fields]').click(); await toast(page, 'Found 1 of 1');
    await check(all(field('account_ending').inputValue().then(v => v === '5678'), asked.find.length === 1 && asked.find[0].join() === 'account_ending'),
        'Find with AI asks only for the empty field and fills it in');
    await check(page.locator('[data-sort-name]').textContent().then(t => t.includes('statement 5678')), 'the found value goes straight into the name');

    // Set up fields with AI: a draft with this document's values, reviewed and kept.
    await page.locator('[data-setup-fields]').click();
    const dialog = page.locator('.kind-setup');
    await dialog.waitFor();
    await check(dialog.locator('tbody tr').count().then(n => n === 4), 'the reader suggests fields for the kind, its own first');
    await check(all(dialog.locator('tbody tr').filter({ hasText: '1204.33' }).locator('input[type=checkbox]').isChecked(),
        dialog.locator('tbody tr').nth(3).locator('input[type=checkbox]').isChecked().then(v => !v)), 'fields with a value in this document start ticked; one it can\'t show does not');
    await check(dialog.locator('tbody tr').nth(2).locator('td').last().textContent().then(t => t.trim() === '980.10'), 'each value is checked against the text and shown');
    await page.screenshot({ path: path.join(shots, 'fields-setup.png') });
    await dialog.locator('[data-keep-fields]').click(); await toast(page, 'Saved 3 fields for Bank statement');
    await check(all(field('beginning_balance').inputValue().then(v => v === '1204.33'), field('ending_balance').inputValue().then(v => v === '980.10')),
        'keeping them fills this document\'s new fields from the draft');

    // Describing a field by hand: the words printed beside it.
    await page.getByRole('button', { name: 'Describe Ending balance for the reader' }).click();
    await page.locator('.field-guide-editor').getByLabel('Printed beside it').fill('Ending Balance, Closing balance');
    await page.locator('.field-guide-editor').getByRole('button', { name: 'Save', exact: true }).click(); await toast(page, 'Saved how to find Ending balance');
    await check(page.getByRole('button', { name: 'Describe Ending balance for the reader' }).getAttribute('class').then(c => c.includes('on')), 'a described field is marked on the slip');
    await page.screenshot({ path: path.join(shots, 'fields-slip.png') });
    await page.locator('.sort-slip h2').click(); await page.keyboard.press('Enter');
    await check(gone(1), 'the statement files with its fields');

    // The next statement: the printed words fill its fields before anyone asks.
    await arrive(2, '980.10', '1,450.00');
    await check(all(field('beginning_balance').inputValue().then(v => v === '980.10'), field('ending_balance').inputValue().then(v => v === '1450.00'),
        field('account_ending').inputValue().then(v => v === '5678')), 'the next one\'s fields all come from their printed words, a masked number by its digits');
    await check(all(asked.find.length === 1 && asked.draft === 1, page.locator('[data-find-fields]').count().then(n => n === 0)), 'nothing more is asked of the reader, and nothing is left to find');
    await check(page.locator('[data-sort-name]').textContent().then(t => t.includes('statement 5678.txt')), 'the name is built from them');

    // The kind form shows the guides for review.
    await page.getByRole('button', { name: 'Edit kind…' }).click();
    await page.locator('.kind-guides summary').waitFor();
    await check(page.getByLabel('What to look for: Beginning balance').inputValue().then(v => v.startsWith('The balance at the start')), 'the kind form shows how each field is found');
    await page.locator('.kind-form').getByRole('button', { name: 'Cancel' }).click();

    await page.setViewportSize({ width: 390, height: 844 });
    await check(noOverflow(page), 'the slip with its field tools fits a phone');
    await check(Promise.resolve(errors.length === 0 && !wireError), 'no page errors and no unexpected model requests');
    console.log(`\n${checks} fields browser checks passed.`);
} catch (error) {
    await page.screenshot({ path: path.join(shots, 'fields-failure.png'), fullPage: true }).catch(() => { });
    console.error(errors, wireError); throw error;
} finally { await browser.close(); fixture.close(); }
