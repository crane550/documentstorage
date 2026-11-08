import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, noOverflow } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5252';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname), 'Fresh local catalog only');
const directory = path.resolve('.local/format-browser-' + Date.now());
execFileSync('python3', ['tests/browser/format-fixture.py', directory]);
const original = await readFile(path.join(directory, 'Synthetic workbook.xlsx'));
const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const page = await browser.newPage({ viewport: { width: 1440, height: 1050 } });
const errors = []; page.on('pageerror', error => errors.push(error.message));
let checks = 0;
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
async function open(name) { await page.locator('.file-name').filter({ has: page.locator('strong', { hasText: new RegExp('^' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$') }) }).click(); await page.locator('.document-preview-heading > strong').filter({ hasText: name }).waitFor(); }
try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, 'Synthetic format archive', directory);
    await open('Synthetic workbook.xlsx');
    const preview = page.locator('.document-preview');
    await preview.getByRole('table', { name: 'Statements cells', exact: true }).waitFor({ timeout: 60000 });
    await check(preview.getByRole('tab', { name: 'Statements', exact: true }).getAttribute('aria-selected').then(value => value === 'true'), 'Excel preview opens the first worksheet with saved cells');
    await check(preview.getByRole('cell', { name: '42.50', exact: true }).count().then(count => count === 2), 'numeric cells and saved formula results display without recalculation');
    await check(preview.getByRole('status').filter({ hasText: 'bounded preview' }).isVisible(), 'a limited worksheet preview clearly reports truncation');
    await preview.getByRole('tab', { name: 'Notes', exact: true }).click();
    await check(preview.getByRole('cell', { name: '<script>window.sheetMustNotRun=true</script>', exact: true }).isVisible(), 'worksheet tabs display literal text even when cells contain markup');
    await check(page.evaluate(() => !window.sheetMustNotRun && document.querySelectorAll('.sheet-table script').length === 0), 'spreadsheet content cannot execute scripts');
    const download = await page.context().request.get(new URL(await preview.getByRole('link', { name: 'Download original', exact: true }).getAttribute('href'), base).href);
    await check(Promise.resolve(download.ok() && Buffer.compare(await download.body(), original) === 0), 'the downloadable original workbook is unchanged');
    await preview.getByRole('button', { name: 'Expand preview', exact: true }).click();
    await check(preview.locator('.document-preview-heading').evaluate(element => { const rect = element.getBoundingClientRect(); return element.contains(document.elementFromPoint(rect.right - 20, rect.top + rect.height / 2)); }), 'expanded content controls remain above the sticky app shell');
    await page.screenshot({ path: '.local/formats-excel-desktop.png', fullPage: false });
    await preview.getByRole('button', { name: 'Close expanded preview', exact: true }).click();

    await open('Synthetic policy.docx');
    await preview.locator('.document-text').filter({ hasText: 'Synthetic training policy' }).waitFor({ timeout: 60000 });
    await check(preview.locator('.document-text').textContent().then(text => text.includes('Synthetic safety team') && text.includes('Revision five') && text.includes('Café')), 'Word text preview includes body, headers, footers and Unicode text');
    await check(preview.getByText('original page layout', { exact: false }).isVisible(), 'Word preview describes its text-only presentation');
    await open('Broken workbook.xlsx');
    await preview.locator('[data-status]').filter({ hasText: /not ready|contents do not match/ }).waitFor({ timeout: 60000 });
    await check(preview.getByRole('link', { name: 'Download original', exact: true }).isVisible(), 'damaged workbooks retain a clear fallback and original download');
    await open('Legacy workbook.xls');
    await preview.locator('[data-status]').filter({ hasText: /contents do not match|not ready/ }).waitFor({ timeout: 60000 });
    await check(preview.getByRole('link', { name: 'Download original', exact: true }).isVisible(), 'mislabeled legacy Excel has a controlled failure and original download');

    await page.goto(base + '/library?q=BeyondPreviewToken'); await ready(page);
    await page.locator('.file-name').filter({ hasText: 'Synthetic workbook.xlsx' }).waitFor();
    await check(true, 'search indexes worksheet content beyond the displayed preview rows');
    await page.goto(base + '/library?q=' + encodeURIComponent('Emergency exits')); await ready(page);
    await page.locator('.file-name').filter({ hasText: 'Synthetic policy.docx' }).waitFor();
    await check(true, 'Word extraction feeds the normal Library search');
    await page.goto(base + '/settings?tab=formats'); await ready(page);
    await page.getByRole('heading', { name: 'File formats', exact: true }).waitFor();
    await check(page.locator('.format-card').filter({ hasText: 'Excel workbook' }).textContent().then(text => text.includes('.xlsx') && text.includes('Extracted text') && text.includes('Worksheets and cells')), 'format capabilities come from the registered Excel module');
    await check(page.locator('.format-card').filter({ hasText: 'PNG image' }).textContent().then(text => text.includes('Extracted text') && text.includes('Tesseract')), 'image capabilities describe local OCR through the registered module');
    await page.getByRole('button', { name: 'Refresh extraction status', exact: true }).click();
    const job = page.locator('.ai-job').filter({ hasText: 'Synthetic policy.docx' }).first();
    await job.getByRole('button', { name: 'Extract text again', exact: true }).click();
    await page.getByRole('status').filter({ hasText: 'Text extraction queued' }).waitFor();
    await check(true, 'a saved file can be explicitly queued for extraction again without asking AI');
    await page.setViewportSize({ width: 390, height: 844 }); await check(noOverflow(page), 'format capabilities and extraction controls fit a phone');
    await page.goto(base + '/library?q=BeyondPreviewToken'); await ready(page); await open('Synthetic workbook.xlsx');
    await preview.getByRole('table', { name: 'Statements cells', exact: true }).waitFor();
    await check(noOverflow(page), 'wide worksheets scroll inside the preview on a phone');
    await page.screenshot({ path: '.local/formats-excel-phone.png', fullPage: false });
    await check(errors.length === 0 && !(await page.locator('#blazor-error-ui').isVisible()), 'format previews and background extraction produce no browser or circuit errors');
    console.log(`All ${checks} format browser checks passed.`);
} catch (error) { await page.screenshot({ path: '.local/formats-failure.png', fullPage: true }).catch(() => {}); throw error; }
finally { await browser.close(); }
