// Search, create, select and return to the same editor against real disposable storage.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile, access, rename } from 'node:fs/promises';
import path from 'node:path';
import { credentials, chromiumPath, signIn, ready, connectStorage, openFolder, useAsInbox, settleAndRescan, toast, noOverflow, folderPicker, chooseFolder } from './ui.mjs';

const base = process.env.DOCUMENTSTORAGE_TEST_URL || 'http://127.0.0.1:5252';
assert(['localhost', '127.0.0.1', '[::1]'].includes(new URL(base).hostname), 'Disposable local catalog only');
const stamp = Date.now(), rootName = 'Picker archive ' + stamp, otherName = 'Other storage ' + stamp;
const directory = path.resolve('.local/picker-browser-' + stamp), otherDirectory = directory + '-other';
for (const folder of ['Intake', 'Archive/Safety Ministry', 'Archive/Other/Safety Ministry', 'Moving/Child', 'Many matches']) await mkdir(path.join(directory, folder), { recursive: true });
await Promise.all(Array.from({ length: 211 }, (_, i) => mkdir(path.join(directory, 'Many matches', 'record-' + String(i).padStart(3, '0')))));
await mkdir(path.join(otherDirectory, 'Elsewhere'), { recursive: true });
await writeFile(path.join(directory, 'Archive/Safety Ministry/Occupied'), 'Synthetic bytes must survive a conflicting folder name.');
await writeFile(path.join(directory, 'Moving/Child/source.txt'), 'Synthetic moved document bytes.');
await writeFile(path.join(directory, 'Intake/Scan 711.txt'), 'Synthetic safety training policy. October 31, 2026. Keep this source in the inbox until approval.');
const browser = await chromium.launch({ executablePath: chromiumPath(), args: ['--no-sandbox', '--disable-dev-shm-usage'] });
const context = await browser.newContext({ viewport: { width: 1440, height: 1050 } });
const page = await context.newPage();
const errors = []; page.on('pageerror', error => errors.push(error.message));
let checks = 0, otherOffline = false;
async function check(value, label) { assert(await value, label); checks++; console.log('PASS ' + label); }
const exists = relative => access(path.join(directory, relative)).then(() => true, () => false);
const item = name => folderPicker(page).getByRole('treeitem').filter({ has: page.locator('strong', { hasText: new RegExp('^' + name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$') }) });
const exactPath = relative => folderPicker(page).locator('.folder-dialog-choice').filter({ has: page.locator('strong') }).and(page.locator('[title=' + JSON.stringify(rootName + ' / ' + relative) + ']'));
async function openParentPicker() { await page.getByRole('dialog', { name: 'New folder', exact: true }).getByRole('button', { name: 'Parent folder', exact: true }).click(); await folderPicker(page).getByLabel('Find a folder', { exact: true }).waitFor(); }
async function createChild(name) {
    await folderPicker(page).getByRole('button', { name: 'New folder', exact: true }).click();
    await folderPicker(page).getByLabel('New folder name', { exact: true }).fill(name);
    await folderPicker(page).getByRole('button', { name: 'Create folder', exact: true }).click();
}
try {
    await page.goto(base); await signIn(page, await credentials());
    await connectStorage(page, base, otherName, otherDirectory);
    await connectStorage(page, base, rootName, directory);
    await page.getByRole('button', { name: 'New folder', exact: true }).click();
    const parentEditor = page.getByRole('dialog', { name: 'New folder', exact: true });
    await parentEditor.getByLabel('Folder name', { exact: true }).fill('Draft stays here');
    const address = page.url();
    await openParentPicker();
    await page.waitForFunction(() => document.activeElement?.hasAttribute('data-folder-search'));
    await check(folderPicker(page).getByLabel('Find a folder', { exact: true }).evaluate(element => document.activeElement === element), 'search receives focus when the picker opens');
    await check(item(otherName).count().then(count => count === 0), 'a create/move picker stays within its storage location');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('Safety');
    await item('Safety Ministry').last().waitFor();
    await check(item('Safety Ministry').count().then(count => count === 2), 'search finds nested folders with the same name');
    await check(folderPicker(page).getByRole('treeitem').allTextContents().then(texts => texts.some(text => text.includes('Archive/Safety Ministry')) && texts.some(text => text.includes('Archive/Other/Safety Ministry'))), 'search results retain parent paths to distinguish matching names');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('record-');
    await folderPicker(page).getByRole('status').filter({ hasText: '211 matching folders' }).waitFor();
    await check(folderPicker(page).locator('.folder-dialog-row.match').count().then(count => count === 200), 'large searches are bounded and clearly report omitted matches');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('no-such-folder');
    await folderPicker(page).getByText('No matching folders.', { exact: false }).waitFor();
    await check(true, 'an empty search gives a useful recovery message');
    await folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).focus();
    await page.keyboard.press('Tab');
    await check(folderPicker(page).getByRole('button', { name: 'Close dialog', exact: true }).evaluate(element => document.activeElement === element), 'Tab stays in the top folder dialog');
    await page.keyboard.press('Escape');
    await folderPicker(page).waitFor({ state: 'hidden' });
    await check((await parentEditor.getByLabel('Folder name', { exact: true }).inputValue()) === 'Draft stays here' && page.url() === address, 'Escape returns to the same unsaved editor without navigation');

    await openParentPicker();
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('Archive/Safety Ministry');
    await exactPath('Archive/Safety Ministry').click();
    const parentId = await exactPath('Archive/Safety Ministry').getAttribute('data-folder-id');
    await createChild('Occupied');
    await folderPicker(page).getByRole('alert').filter({ hasText: 'already exists' }).waitFor();
    await check(readFile(path.join(directory, 'Archive/Safety Ministry/Occupied'), 'utf8').then(text => text.startsWith('Synthetic bytes')), 'a conflicting name preserves the file and keeps the picker open');
    await folderPicker(page).getByLabel('New folder name', { exact: true }).fill('../escape');
    await folderPicker(page).getByRole('button', { name: 'Create folder', exact: true }).click();
    await folderPicker(page).getByRole('alert').filter({ hasText: 'without slashes' }).waitFor();
    await check(true, 'folder creation uses the real portable-name validation');

    // Another tab changes the selected parent's path while the create box stays open.
    const other = await context.newPage();
    await other.goto(base + '/library?folder=' + parentId); await ready(other);
    await other.getByRole('button', { name: 'Folder actions', exact: true }).click();
    await other.getByRole('menuitem', { name: 'Rename or move folder', exact: true }).click();
    await other.getByLabel('Name', { exact: true }).fill('Safety Ministry Renamed');
    await other.getByRole('dialog').getByRole('button', { name: 'Save', exact: true }).click();
    await other.getByRole('dialog').waitFor({ state: 'hidden' }); await other.close();
    await folderPicker(page).getByLabel('New folder name', { exact: true }).fill('Training');
    await folderPicker(page).getByRole('button', { name: 'Create folder', exact: true }).click();
    await folderPicker(page).getByRole('alert').filter({ hasText: 'parent folder changed' }).waitFor();
    await check(!(await exists('Archive/Safety Ministry')) && !(await exists('Archive/Safety Ministry Renamed/Training')), 'a changed parent cannot silently redirect creation or recreate its old path');
    await folderPicker(page).getByRole('button', { name: 'Refresh folders', exact: true }).click();
    await folderPicker(page).getByText('Create inside', { exact: false }).filter({ hasText: 'Safety Ministry Renamed' }).waitFor();
    await folderPicker(page).getByRole('button', { name: 'Create folder', exact: true }).click();
    await folderPicker(page).getByRole('status').filter({ hasText: 'Folder created and selected.' }).waitFor();
    await check(exists('Archive/Safety Ministry Renamed/Training'), 'refreshing permits creation at the explicitly updated parent');
    await check(item('Training').getAttribute('aria-selected').then(value => value === 'true'), 'the new folder is selected and revealed in the tree');
    await item('Training').focus(); await page.keyboard.press('ArrowLeft');
    await page.waitForFunction(id => document.activeElement?.getAttribute('data-folder-id') === id, parentId);
    await check(true, 'Left arrow from a leaf focuses its parent');
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction(() => document.activeElement?.textContent.includes('Training'));
    await check(true, 'Right arrow on an open parent focuses its child');
    await page.screenshot({ path: '.local/folder-picker-desktop.png', fullPage: false });
    await folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).click();
    await folderPicker(page).waitFor({ state: 'hidden' });
    await check((await parentEditor.getByLabel('Folder name', { exact: true }).inputValue()) === 'Draft stays here', 'creating and selecting a parent preserves the draft folder name');
    await parentEditor.getByRole('button', { name: 'Create folder', exact: true }).click();
    await parentEditor.waitFor({ state: 'hidden' });
    await check(exists('Archive/Safety Ministry Renamed/Training/Draft stays here'), 'the original new-folder dialog creates inside the selected parent');

    // The move editor can create a new destination without losing the pending rename.
    await openFolder(page, 'Moving');
    await page.getByRole('button', { name: 'Folder actions', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Rename or move folder', exact: true }).click();
    const moveEditor = page.getByRole('dialog', { name: 'Rename or move', exact: true });
    await moveEditor.getByLabel('Name', { exact: true }).fill('Moved');
    await moveEditor.getByRole('button', { name: 'Destination folder', exact: true }).click();
    await check((await item('Moving').count()) === 0 && (await item('Child').count()) === 0, 'moving folders excludes themselves and their descendants');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('Archive'); await item('Archive').click();
    await createChild('Move destination');
    await folderPicker(page).getByRole('status').filter({ hasText: 'Folder created and selected.' }).waitFor();
    await folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).click();
    await folderPicker(page).waitFor({ state: 'hidden' });
    await moveEditor.getByRole('button', { name: 'Save', exact: true }).click(); await moveEditor.waitFor({ state: 'hidden' });
    await check(readFile(path.join(directory, 'Archive/Move destination/Moved/Child/source.txt'), 'utf8').then(text => text === 'Synthetic moved document bytes.'), 'a pending rename and move use the newly created destination through the journal');

    // Rule destinations share the same picker and preserve unsaved rule fields.
    await page.goto(base + '/rules'); await ready(page);
    await page.getByRole('button', { name: 'New rule', exact: true }).first().click();
    const rule = page.getByRole('dialog', { name: 'New rule', exact: true });
    await rule.getByLabel('Rule name', { exact: true }).fill('Keep this unsaved rule');
    await rule.getByRole('button', { name: 'Destination folder', exact: true }).click();
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill(otherName);
    await item(otherName).waitFor();
    await check(item(otherName).count().then(count => count === 1), 'a rule destination picker can search across registered storage locations');
    await rename(otherDirectory, otherDirectory + '-offline'); otherOffline = true;
    async function scanFromAnotherTab() {
        const tab = await context.newPage(); await tab.goto(base + '/library'); await ready(tab);
        await tab.getByRole('button', { name: 'Rescan storage', exact: true }).click(); await toast(tab, 'Storage checked.'); await tab.close();
    }
    await scanFromAnotherTab();
    await folderPicker(page).getByRole('button', { name: 'Refresh folders', exact: true }).click();
    await item(otherName).filter({ hasText: 'Offline' }).waitFor(); await item(otherName).click();
    await folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).and(page.locator(':disabled')).waitFor();
    await check(folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).isDisabled(), 'offline storage remains visible but cannot be selected');
    await check(folderPicker(page).getByRole('button', { name: 'New folder', exact: true }).isDisabled(), 'folder creation is disabled on offline storage');
    await rename(otherDirectory + '-offline', otherDirectory); otherOffline = false;
    await scanFromAnotherTab(); await folderPicker(page).getByRole('button', { name: 'Refresh folders', exact: true }).click();
    await chooseFolder(page, 'Archive/Move destination', 'Move destination');
    await check((await rule.getByLabel('Rule name', { exact: true }).inputValue()) === 'Keep this unsaved rule' && (await rule.getByRole('button', { name: 'Destination folder', exact: true }).textContent()).includes('Move destination'), 'selecting a rule destination retains the rest of the editor');
    await rule.getByRole('button', { name: 'Cancel', exact: true }).click();

    // Filing picker excludes inbox branches and never files on Enter inside the popup.
    await openFolder(page, 'Intake'); await useAsInbox(page); await settleAndRescan(page);
    await page.goto(base + '/'); await ready(page);
    await page.locator('.intake-item').filter({ hasText: 'Scan 711.txt' }).click();
    await page.locator('.sort-slip h2').filter({ hasText: 'Scan 711.txt' }).waitFor();
    await page.locator('#sort-kind').fill('Everything else'); await page.locator('#sort-kind').press('Tab');
    const title = page.locator('#sort-title');
    await title.fill('Unsaved safety policy'); await title.press('Tab');
    await page.locator('[data-sort-path]').waitFor();
    await page.getByRole('group', { name: 'How the folder is chosen' }).getByRole('button', { name: 'Choose a folder', exact: true }).click();
    await check(item('Intake').count().then(count => count === 0), 'filing destinations exclude the inbox branch');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).waitFor();
    await page.evaluate(() => document.activeElement.blur()); await page.keyboard.press('Enter');
    await check((await exists('Intake/Scan 711.txt')) && (await folderPicker(page).isVisible()), 'background filing shortcuts stay disabled even if dialog focus is temporarily lost');
    await folderPicker(page).getByLabel('Find a folder', { exact: true }).fill('Archive/Move destination');
    await item('Move destination').click();
    await folderPicker(page).getByRole('button', { name: 'New folder', exact: true }).click();
    await folderPicker(page).getByLabel('New folder name', { exact: true }).fill('Inbox choice');
    await folderPicker(page).getByLabel('New folder name', { exact: true }).press('Enter');
    await folderPicker(page).getByRole('status').filter({ hasText: 'Folder created and selected.' }).waitFor();
    await check((await exists('Intake/Scan 711.txt')) && (await exists('Archive/Move destination/Inbox choice')), 'Enter creates only the folder and does not approve or move the inbox document');
    await page.setViewportSize({ width: 390, height: 844 });
    await check(noOverflow(page), 'the tree picker fits a phone without sideways scrolling');
    await check(folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).evaluate(element => { const r = element.getBoundingClientRect(); return r.bottom <= innerHeight && r.top >= 0 && r.left >= 0 && r.right <= innerWidth; }), 'folder selection remains fully inside the phone viewport');
    await page.screenshot({ path: '.local/folder-picker-phone.png', fullPage: false });
    await folderPicker(page).getByRole('button', { name: 'Use this folder', exact: true }).click();
    await folderPicker(page).waitFor({ state: 'hidden' });
    await check((await title.inputValue()) === 'Unsaved safety policy', 'inline folder creation preserves unsaved document details');
    await page.waitForFunction(() => document.querySelector('[data-sort-path]')?.textContent.includes('Inbox choice'));
    await check(exists('Intake/Scan 711.txt'), 'choosing a filing destination previews the path and leaves the source untouched');
    await check(errors.length === 0 && !(await page.locator('#blazor-error-ui').isVisible()), 'folder selection workflows produce no browser or circuit errors');
    console.log(`All ${checks} folder picker browser checks passed.`);
} catch (error) {
    await page.screenshot({ path: '.local/folder-picker-failure.png', fullPage: true }).catch(() => {});
    throw error;
} finally {
    if (otherOffline) await rename(otherDirectory + '-offline', otherDirectory);
    await browser.close();
}
