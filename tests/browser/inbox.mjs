import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile, access } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, openFolder, useAsInbox, settleAndRescan, inbox, nav, openInboxItem, toast, noOverflow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5252';
assert(['localhost','127.0.0.1','[::1]'].includes(new URL(base).hostname), 'Disposable local instance only');
const directory = path.resolve('.local/inbox-browser-' + Date.now());
await mkdir(path.join(directory, 'Workbench intake'), { recursive: true });
for (let i = 0; i < 102; i++) await writeFile(path.join(directory, 'Workbench intake', `work-${String(i).padStart(3,'0')}.txt`), `Synthetic inbox document ${i}. Paid tool receipt.`);
const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox','--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1600, height: 1050 } });
const errors = []; page.on('pageerror', e => errors.push(e.message)); let checks = 0;
async function check(value, message) { assert(await value, message); checks++; console.log('PASS ' + message); }
async function selected(name) { await page.locator('.slip-head h2').filter({ hasText: name }).waitFor(); }
try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Inbox workbench checks', directory);
    await openFolder(page, 'Workbench intake'); await useAsInbox(page); await settleAndRescan(page); await inbox(page);
    await openInboxItem(page, 'work-041.txt');
    await nav(page, 'Rules'); await inbox(page); await selected('work-041.txt');
    await check(Promise.resolve(true), 'returning to Inbox restores the selected document');
    await page.reload(); await ready(page); await selected('work-041.txt');
    await check(Promise.resolve(true), 'selected document survives a full browser reload');
    await page.locator('.extraction-status').getByRole('button', { name: 'Rescan text', exact: true }).click(); await toast(page, 'Text rescanned:');
    await check(page.locator('.slip-text.review-text pre').textContent().then(t => t === 'Synthetic inbox document 41. Paid tool receipt.'), 'per-document rescan displays fresh extracted text');
    await page.locator('.slip-tools').getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Delete document…', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await check(readFile(path.join(directory, 'Workbench intake/work-041.txt'), 'utf8').then(t => t.includes('document 41')), 'cancelling delete preserves the incoming file');
    await page.locator('.slip-tools').getByRole('button', { name: 'More actions', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Delete document…', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Move to trash', exact: true }).click(); await toast(page, 'Document moved to Trash.');
    await selected('work-042.txt');
    await check(access(path.join(directory, 'Workbench intake/work-041.txt')).then(() => false, () => true), 'confirmed delete removes the source and advances to the next document');
    await page.goto(base + '/history?tab=trash'); await ready(page);
    const trashRow = page.locator('.activity-row').filter({ hasText: 'work-041.txt' });
    await trashRow.getByRole('button', { name: /Restore/ }).click(); await toast(page, 'work-041.txt restored');
    await check(readFile(path.join(directory, 'Workbench intake/work-041.txt'), 'utf8').then(t => t.includes('document 41')), 'inbox deletion remains restorable with its original bytes');
    await inbox(page); await selected('work-042.txt');
    await page.locator('.tray-pager').getByRole('button', { name: 'Next', exact: true }).click(); await selected('work-100.txt');
    await openInboxItem(page, 'work-101.txt');
    const id = await page.locator('.slip-tools a[download]').getAttribute('href').then(x => x.split('/')[2]);
    await nav(page, 'Rules'); await inbox(page); await selected('work-101.txt');
    await check(page.locator('.tray-pager').textContent().then(t => t.includes('101–102')), 'saved selection restores the correct page');
    await page.locator('.tray-pager').getByRole('button', { name: 'Previous', exact: true }).click();
    await page.evaluate(url => Blazor.navigateTo(url), base + '/?open=' + id); await selected('work-101.txt');
    await check(Promise.resolve(true), 'an explicit link opens a document beyond the first inbox page');
    for (const name of ['work-101.txt', 'work-100.txt']) {
        await selected(name);
        await page.locator('.slip-tools').getByRole('button', { name: 'More actions', exact: true }).click();
        await page.getByRole('menuitem', { name: 'Delete document…', exact: true }).click();
        await page.getByRole('dialog').getByRole('button', { name: 'Move to trash', exact: true }).click();
        await toast(page, 'Document moved to Trash.');
    }
    await selected('work-099.txt');
    await check(page.locator('.intake-item').count().then(n => n === 100), 'emptying the last page returns near the end of the preceding page');
    await page.setViewportSize({ width: 390, height: 844 });
    await check(noOverflow(page), 'inbox tools fit the mobile viewport');
    await check(errors.length === 0, 'inbox navigation, rescan and trash produce no browser errors');
    console.log(`All ${checks} inbox browser checks passed.`);
} finally { await browser.close(); }
