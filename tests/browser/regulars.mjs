// Regulars in a real browser: three monthly statements approved after reading; the fourth fills
// in from memory with no reading; after five right in a row the Inbox offers to let them file
// themselves; the next one files itself, shows in Filed, and "Looks right" confirms it.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, signIn, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, ready, chromiumPath } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5250';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname));
const directory = path.resolve('.local/regulars-browser-' + Date.now());
await mkdir(path.join(directory, 'Inbox'), { recursive: true });
const month = m => new Date(Date.UTC(2026, m - 1, 1)).toLocaleString('en-US', { month: 'long', timeZone: 'UTC' });
const days = m => new Date(Date.UTC(2026, m, 0)).getUTCDate();
const statement = m => `WILLOW SAVINGS\nStatement period ${month(m)} 1, 2026 to ${month(m)} ${days(m)}, 2026\nAccount ending 1234\nSynthetic balance ${m}0.00`;
const name = m => `scan-2026${String(m).padStart(2, '0')}.txt`;

let reads = 0, wireError;
const fixture = http.createServer(async (req, res) => {
    try {
        res.setHeader('Content-Type', 'application/json');
        if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: 'synthetic-sorter:latest', size: 123 }] }));
        let raw = ''; for await (const chunk of req) raw += chunk;
        if (req.url === '/api/show') return res.end('{"capabilities":["completion"]}');
        if (req.url === '/api/chat') {
            const input = JSON.parse(JSON.parse(raw).messages[1].content); reads++;
            const m = Number(input.file_name.slice(9, 11));
            const answer = { kind: 'Bank statement', kind_description: '', from: 'Willow Savings', date: `2026-${String(m).padStart(2, '0')}-${days(m)}`, title: 'Savings statement', tags: [], fields: [{ name: 'account_ending', value: '1234', quote: 'ending 1234' }], confidence: 'high' };
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
const inInbox = m => access(path.join(directory, 'Inbox', name(m))).then(() => true, () => false);
async function gone(m, wait = 60) { for (let i = 0; i < wait * 4; i++) { if (!await inInbox(m)) return true; await new Promise(r => setTimeout(r, 250)); } return false; }
async function arrive(m) { await writeFile(path.join(directory, 'Inbox', name(m)), statement(m)); await page.goto(base + '/library'); await ready(page); await settleAndRescan(page); await settleAndRescan(page); await page.goto(base + '/'); await ready(page); }
async function open(m) {
    await page.locator('.sort-row').filter({ has: page.locator('strong', { hasText: name(m) }) }).locator('.intake-item').click();
    await page.locator('.sort-slip h2').filter({ hasText: name(m) }).waitFor();
}
async function approveWithEnter(m) { await open(m); await page.locator('.sort-slip h2').click(); await page.keyboard.press('Enter'); assert(await gone(m), 'filed ' + name(m)); }
const shots = path.resolve('.local');

try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Regulars test archive', directory);
    await openFolder(page, 'Inbox'); await useAsInbox(page);
    await page.goto(base + '/settings?tab=reader'); await ready(page);
    await page.getByLabel('Ollama server address', { exact: true }).fill(endpoint);
    await page.getByRole('button', { name: 'Check connection & list models', exact: true }).click(); await toast(page, 'Connected.');
    await page.getByRole('button', { name: 'synthetic-sorter:latest', exact: true }).click();
    await page.getByText('Use the AI reader', { exact: true }).click();
    await page.getByText('Read new inbox documents automatically', { exact: true }).click();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');

    // Three months read and approved.
    for (const m of [1, 2, 3]) {
        await arrive(m);
        for (let i = 0; i < 120 && reads < m; i++) await new Promise(r => setTimeout(r, 500));
        await page.reload(); await ready(page);
        await page.locator('.sort-row').filter({ hasText: name(m) }).locator('.stamp').filter({ hasNotText: /Queued|Reading/ }).waitFor();
        await approveWithEnter(m);
    }
    await check(reads === 3, 'the first three are read by the reader');

    // Month four: from memory, no reading.
    await arrive(4);
    await new Promise(r => setTimeout(r, 2500));
    await open(4);
    await check(page.locator('.sort-reader').textContent().then(t => t.includes('Filled in from the last 3 bank statements from Willow Savings, no reading needed')), 'the fourth fills in from memory');
    await check(page.locator('[data-sort-name]').textContent().then(t => t.includes('2026-04 Willow Savings statement 1234.txt')), 'memory picks the date and account the same way as before');
    await check(Promise.resolve(reads === 3), 'no model reading is spent on it');
    await page.screenshot({ path: path.join(shots, 'regulars-memory.png') });
    await page.locator('.sort-slip h2').click(); await page.keyboard.press('Enter'); await check(gone(4), 'Enter files it');
    await arrive(5); await approveWithEnter(5);

    // The nudge.
    await page.reload(); await ready(page);
    await page.locator('.nudge').filter({ hasText: 'Bank statement from Willow Savings' }).waitFor();
    await check(page.locator('.nudge').textContent().then(t => t.includes('right 5 times in a row')), 'after five right in a row the Inbox offers to let them file themselves');
    await page.screenshot({ path: path.join(shots, 'regulars-nudge.png') });
    await page.getByRole('button', { name: 'Let them file themselves' }).click(); await toast(page, 'will now file itself');

    // Month six files itself.
    await arrive(6);
    await check(gone(6, 60), 'the next statement files itself in the background');
    await page.getByRole('button', { name: 'Filed', exact: true }).click();
    const auto = page.locator('.filed-row').filter({ hasText: '2026-06 Willow Savings statement 1234.txt' });
    await check(auto.locator('.stamp').textContent().then(t => t.includes('Filed itself')), 'Filed shows it filed itself');
    await auto.getByRole('button', { name: 'Looks right' }).click();
    await auto.getByText('Confirmed').waitFor();
    await check(Promise.resolve(true), 'Looks right confirms it');
    await page.goto(base + '/kinds?tab=regulars'); await ready(page);
    const row = page.locator('.regular-row').filter({ hasText: 'Bank statement from Willow Savings' });
    await check(row.locator('input[type=checkbox]').isChecked(), 'the Regulars tab shows it filing itself');
    await check(row.textContent().then(t => t.includes('6 filed')), 'the confirmation counts toward its record');
    await page.screenshot({ path: path.join(shots, 'regulars-tab.png'), fullPage: true });
    await check(errors.length === 0 && !wireError, 'no page errors');
    console.log(`\n${checks} regulars browser checks passed.`);
} catch (error) {
    await page.screenshot({ path: path.join(shots, 'regulars-failure.png'), fullPage: true }).catch(() => { });
    console.error(errors, wireError); throw error;
} finally { await browser.close(); fixture.close(); }
