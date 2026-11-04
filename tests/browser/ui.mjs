// Shared steps for the browser checks: how a person signs in, connects storage and moves around.
// Keeping them here means a layout change updates one file, and each check reads as the workflow it proves.
import { readFile } from 'node:fs/promises';

export async function credentials() {
    if (process.env.DOCUMENTSTORAGE_TEST_USERNAME && process.env.DOCUMENTSTORAGE_TEST_PASSWORD)
        return { username: process.env.DOCUMENTSTORAGE_TEST_USERNAME, password: process.env.DOCUMENTSTORAGE_TEST_PASSWORD };
    return JSON.parse(await readFile('.local/browser-credentials.json', 'utf8'));
}

export const chromiumPath = () => process.env.DOCUMENTSTORAGE_CHROMIUM || '/usr/bin/chromium';

/** Signs in from wherever the browser is and waits for the interactive workspace. */
export async function signIn(page, login) {
    await page.getByLabel('Username', { exact: true }).fill(login.username);
    await page.getByLabel('Password', { exact: true }).fill(login.password);
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    await ready(page);
}

/** The circuit is interactive once the drop handler is attached to the "Add to inbox" control. */
export async function ready(page) {
    await page.locator('.shell').waitFor();
    await page.waitForFunction(() => window.Blazor !== undefined && document.querySelector('.intake-upload')?.dataset.dropReady === 'true');
    await page.locator('.folder-tree .tree-branch, .folder-tree .tree-empty').first().waitFor();
}

export const nav = (page, name) => page.locator('.sidebar').getByRole('link', { name, exact: false }).first().click();
export const inbox = page => page.locator('.main-nav a[href="/"]').click();

/** Connects a storage location, or opens it when this catalog already has it. */
export async function connectStorage(page, base, name, directory) {
    const existing = page.locator('.tree-top > .tree-row .tree-label').filter({ has: page.locator('span', { hasText: new RegExp('^' + escape(name) + '$') }) });
    if (await existing.count() > 0) { await existing.first().click(); await page.locator('.page-head h1').filter({ hasText: name }).waitFor(); return; }
    await page.goto(base + '/settings?tab=storage'); await ready(page);
    await page.locator('.add-card').click();
    await page.getByLabel('Location name', { exact: true }).fill(name);
    await page.getByLabel('Full path on the server', { exact: true }).fill(directory);
    await page.getByRole('dialog').getByRole('button', { name: 'Connect storage', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await page.locator('.page-head h1').filter({ hasText: name }).waitFor();
}

/** Opens a folder from the sidebar tree by its exact name. */
export async function openFolder(page, name) {
    await page.locator('.tree-label').filter({ has: page.locator('span', { hasText: new RegExp('^' + escape(name) + '$') }) }).first().click();
    await page.locator('.page-head h1').filter({ hasText: new RegExp('^' + escape(name) + '$') }).waitFor();
}

/** Opens a child folder from the current folder's listing. */
export async function enterFolder(page, name) {
    await fileRow(page, name).click();
    await page.locator('.page-head h1').filter({ hasText: new RegExp('^' + escape(name) + '$') }).waitFor();
}

/** Creates a folder inside the folder currently open in the Library. */
export async function newFolder(page, name) {
    await page.getByRole('button', { name: 'New folder', exact: true }).click();
    await page.getByLabel('Folder name', { exact: true }).fill(name);
    await page.getByRole('dialog').getByRole('button', { name: 'Create folder', exact: true }).click();
    await page.getByRole('dialog').waitFor({ state: 'hidden' });
    await fileRow(page, name).waitFor();
}

export const fileRow = (page, name) => page.locator('.file-name').filter({ has: page.locator('strong', { hasText: new RegExp('^' + escape(name) + '$') }) });
export const toast = (page, text) => page.getByRole('status').filter({ hasText: text }).first().waitFor();

/** Makes the open folder an inbox. */
export async function useAsInbox(page) {
    await page.getByRole('button', { name: 'Use as inbox', exact: true }).click();
    await page.getByRole('button', { name: 'Stop using as inbox', exact: true }).waitFor();
}

/** Waits out the scanner's stability window, then rescans so new files are ready to review. */
export async function settleAndRescan(page) {
    await new Promise(resolve => setTimeout(resolve, 5500));
    await page.getByRole('button', { name: 'Rescan storage', exact: true }).click();
    await toast(page, 'Storage checked.');
}

export const noOverflow = page => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth);
const escape = text => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Opens an inbox document and waits until its own filing slip is showing. */
export async function openInboxItem(page, name) {
    await page.locator('.intake-item').filter({ hasText: name }).click();
    await page.locator('.slip-head h2').filter({ hasText: name }).waitFor();
}
