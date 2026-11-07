// Duplicates in a real browser: a bill already in the library arrives again in the inbox (twice);
// the Inbox stamps it, the slip says where the other copy is, Enter won't file it, Keep both lets
// it through, and Delete this copy sends the other copy to Trash. Synthetic files only.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, signIn, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, ready, chromiumPath, openInboxItem, noOverflow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5250';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname));
const directory = path.resolve('.local/duplicates-browser-' + Date.now());
const bill = 'SYNTHETIC WATER WORKS\nBill date March 3, 2026\nAmount due 42.10\nThis synthetic bill exists only for the duplicate browser check.';
await mkdir(path.join(directory, 'Inbox'), { recursive: true });
await mkdir(path.join(directory, 'Old papers'), { recursive: true });
await writeFile(path.join(directory, 'Old papers', 'water march.txt'), bill);

const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
const errors = []; page.on('pageerror', e => errors.push(e.message));
let checks = 0;
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
const exists = name => access(path.join(directory, 'Inbox', name)).then(() => true, () => false);
const shots = path.resolve('.local');

try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Duplicates test archive', directory);
    await openFolder(page, 'Inbox'); await useAsInbox(page);
    await writeFile(path.join(directory, 'Inbox', 'scan-a.txt'), bill);
    await writeFile(path.join(directory, 'Inbox', 'scan-b.txt'), bill);
    await settleAndRescan(page); await settleAndRescan(page);
    await page.goto(base + '/'); await ready(page);

    await check(page.locator('.sort-row').filter({ hasText: 'scan-a.txt' }).locator('.stamp').textContent().then(t => t.includes('Duplicate')), 'the Inbox stamps a copy of something already in Stash');
    await openInboxItem(page, 'scan-a.txt');
    const banner = page.locator('[data-duplicates]');
    await check(banner.textContent().then(t => t.includes('Already in Stash') && t.includes('Old papers/water march.txt') && t.includes('the same file')), 'the slip says where the other copy is');
    await check(banner.textContent().then(t => t.includes('scan-b.txt') && t.includes('also waiting in the inbox')), 'a second copy in the inbox is named too');
    await check(banner.locator('a', { hasText: 'Old papers/water march.txt' }).getAttribute('href').then(h => h.startsWith('/library?folder=') && h.includes('&doc=')), 'the other copy opens in the Library');
    await page.screenshot({ path: path.join(shots, 'duplicates-slip.png') });

    // Enter doesn't file a copy by accident.
    await page.locator('#sort-kind').fill('Letter'); await page.locator('#sort-title').fill('Water bill');
    await page.locator('[data-sort-name]').filter({ hasText: 'Water bill' }).waitFor();
    await page.locator('.sort-slip h2').click(); await page.keyboard.press('Enter');
    await page.getByRole('alert').filter({ hasText: 'This is a copy of a document already in Stash' }).first().waitFor();
    await page.locator('.toast.error button').first().click().catch(() => { });
    await check(exists('scan-a.txt'), 'Enter leaves the copy in the inbox and says why');

    // Delete this copy: the other inbox copy goes to Trash.
    await openInboxItem(page, 'scan-b.txt');
    await page.locator('[data-delete-copy]').click();
    await page.getByRole('dialog').getByRole('button', { name: 'Move to trash', exact: true }).click(); await toast(page, 'Document moved to Trash.');
    await check(exists('scan-b.txt').then(v => !v), 'Delete this copy moves it to Trash');

    // Keep both: filed anyway, because the person said so.
    await openInboxItem(page, 'scan-a.txt');
    await check(page.locator('[data-duplicates]').textContent().then(t => t.includes('in Trash')), 'a copy in Trash still shows, and says so');
    await page.locator('[data-keep-both]').click();
    await check(page.locator('[data-duplicates]').waitFor({ state: 'detached', timeout: 5000 }).then(() => true, () => false), 'Keep both puts the notice away');
    await page.locator('#sort-kind').fill('Letter'); await page.locator('#sort-title').fill('Water bill');
    await page.locator('[data-sort-name]').filter({ hasText: 'Water bill' }).waitFor();
    await page.locator('.sort-slip h2').click(); await page.keyboard.press('Enter');
    for (let i = 0; i < 80 && await exists('scan-a.txt'); i++) await new Promise(r => setTimeout(r, 250));
    await check(exists('scan-a.txt').then(v => !v), 'after Keep both, Enter files it');

    await page.setViewportSize({ width: 390, height: 844 });
    await check(noOverflow(page), 'the Inbox fits a phone');
    await check(Promise.resolve(errors.length === 0), 'no page errors');
    console.log(`\n${checks} duplicate browser checks passed.`);
} catch (error) {
    await page.screenshot({ path: path.join(shots, 'duplicates-failure.png'), fullPage: true }).catch(() => { });
    console.error(errors); throw error;
} finally { await browser.close(); }
