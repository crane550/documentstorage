// Synthetic readers and documents only; exercise the actual settings, worker and saved results.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, noOverflow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5252';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname), 'Disposable local app only');
let desktopOnline = false, desktopReads = 0, fallbackReads = 0, wireError;
const answer = { kind: 'Receipt', kind_description: '', from: 'Synthetic Hardware Store', date: '2026-10-31', title: 'Workshop supplies', tags: [], fields: [], confidence: 'medium' };
async function fixture(desktop) {
    const server = http.createServer(async (req, res) => {
        try {
            res.setHeader('Content-Type', 'application/json');
            const model = desktop ? 'synthetic-desktop:8b' : 'synthetic-fallback:4b';
            if (desktop && !desktopOnline) { res.statusCode = 503; return res.end('{}'); }
            if (req.url === '/api/tags') return res.end(JSON.stringify({ models: [{ name: model, size: 123 }] }));
            let body = ''; for await (const part of req) body += part;
            const input = JSON.parse(body); assert.equal(input.model, model);
            if (req.url === '/api/show') return res.end('{"capabilities":["completion"]}');
            if (req.url === '/api/chat') {
                assert.equal(input.keep_alive, 45); assert.equal(input.think, false);
                if (desktop) desktopReads++; else fallbackReads++;
                return res.end(JSON.stringify({ done: true, message: { content: JSON.stringify(answer) } }));
            }
            res.statusCode = 404; res.end('{}');
        } catch (error) { wireError = error; res.statusCode = 500; res.end('{}'); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return { server, endpoint: 'http://127.0.0.1:' + server.address().port };
}
const desktop = await fixture(true), fallback = await fixture(false);
const directory = path.resolve('.local/reader-browser-' + Date.now());
await mkdir(path.join(directory, 'Intake'), { recursive: true });
await writeFile(path.join(directory, 'Intake', 'Scan 601.txt'), 'Synthetic Hardware Store\nRECEIPT\nOctober 31, 2026\nWorkshop supplies\nTotal 42.50');
const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 1050 } });
const errors = []; page.on('pageerror', error => errors.push(error.message));
let checks = 0;
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
async function readerPage() { await page.goto(base + '/settings?tab=reader'); await ready(page); await page.locator('.reader-target').first().waitFor(); }
const row = number => page.getByRole('region', { name: 'Reader ' + number, exact: true });
async function until(predicate) { for (let i = 0; i < 120; i++) { if (predicate()) return; await new Promise(resolve => setTimeout(resolve, 500)); } assert.fail('Reader did not complete'); }
try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Reader browser archive', directory);
    await openFolder(page, 'Intake'); await useAsInbox(page); await settleAndRescan(page);
    await readerPage();
    await row(1).getByLabel('Reader name', { exact: true }).fill('Work desktop');
    await row(1).getByLabel('Ollama server address', { exact: true }).fill(desktop.endpoint);
    await row(1).getByLabel('Installed model', { exact: true }).fill('synthetic-desktop:8b');
    await page.getByRole('button', { name: 'Add fallback reader', exact: true }).click();
    await row(2).getByLabel('Reader name', { exact: true }).fill('Dell server');
    await row(2).getByLabel('Ollama server address', { exact: true }).fill(fallback.endpoint);
    await row(2).getByRole('button', { name: 'Check connection & list models', exact: true }).click();
    await toast(page, 'Connected.');
    await row(2).getByRole('button', { name: 'synthetic-fallback:4b', exact: true }).click();
    await page.locator('.advanced > summary').click();
    await page.getByLabel('Unload idle models after (seconds)', { exact: true }).fill('45');
    await page.getByLabel('Use the AI reader', { exact: true }).check();
    await page.getByLabel('Read new inbox documents automatically', { exact: true }).check();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');
    await readerPage();
    await check((await row(1).getByLabel('Reader name', { exact: true }).inputValue()) === 'Work desktop' && (await row(2).getByLabel('Installed model', { exact: true }).inputValue()) === 'synthetic-fallback:4b', 'server priorities and different model choices survive navigation');
    await until(() => fallbackReads === 1);
    await page.getByRole('button', { name: 'Refresh reading status', exact: true }).click();
    await page.locator('.ai-job').filter({ hasText: 'Dell server' }).filter({ hasText: 'Proposed' }).waitFor();
    await check(desktopReads === 0 && fallbackReads === 1, 'an unavailable preferred reader falls back once through the real worker');
    await check(page.locator('.ai-job').first().textContent().then(text => text.includes('Dell server') && text.includes('synthetic-fallback:4b')), 'reading history identifies the actual reader and model');
    await page.goto(base + '/'); await ready(page);
    await page.locator('.sort-row').filter({ hasText: 'Scan 601.txt' }).waitFor();
    await page.goto(base + '/library'); await ready(page);
    await page.goto(base + '/'); await ready(page);
    await check(fallbackReads === 1 && desktopReads === 0, 'returning to the Inbox uses its saved reading');

    // Let the preferred reader's 30-second availability cooldown expire, then test a new file.
    desktopOnline = true;
    await new Promise(resolve => setTimeout(resolve, 31000));
    await writeFile(path.join(directory, 'Intake', 'Scan 602.txt'), 'Synthetic Hardware Store\nRECEIPT\nOctober 31, 2026\nDifferent supplies\nTotal 55.20');
    await readerPage();
    await page.getByRole('button', { name: 'Rescan storage', exact: true }).click();
    await new Promise(resolve => setTimeout(resolve, 6000));
    await page.getByRole('button', { name: 'Rescan storage', exact: true }).click();
    await until(() => desktopReads === 1);
    await check(fallbackReads === 1, 'the recovered preferred reader handles the next document without rereading the first');

    await page.getByRole('button', { name: 'Move reader 2 up', exact: true }).click();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');
    await readerPage();
    await check((await row(1).getByLabel('Reader name', { exact: true }).inputValue()) === 'Dell server', 'moving a fallback up persists it as the preferred reader');
    await row(2).getByLabel('Use this fallback', { exact: true }).uncheck();
    await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader on');
    await readerPage();
    await check(!(await row(2).getByLabel('Use this fallback', { exact: true }).isChecked()), 'a fallback can be disabled without removing its configuration');
    await page.setViewportSize({ width: 390, height: 844 });
    await check(noOverflow(page), 'multiple reader settings fit a phone viewport');
    await check(!wireError && errors.length === 0, 'reader settings and fallback produce no protocol or browser errors');
    console.log(`All ${checks} reader browser checks passed.`);
} finally {
    try {
        await page.setViewportSize({ width: 1440, height: 1050 });
        await openFolder(page, 'Intake');
        await page.getByRole('button', { name: 'Stop using as inbox', exact: true }).click();
        await page.getByRole('button', { name: 'Use as inbox', exact: true }).waitFor();
        await readerPage();
        while (await page.locator('.reader-target').count() > 1) await row(2).getByRole('button', { name: 'Remove', exact: true }).click();
        await page.getByLabel('Use the AI reader', { exact: true }).uncheck();
        await page.getByRole('button', { name: 'Save reader settings', exact: true }).click(); await toast(page, 'AI reader off');
    } catch { }
    await browser.close(); desktop.server.closeAllConnections(); fallback.server.closeAllConnections(); desktop.server.close(); fallback.server.close();
}
