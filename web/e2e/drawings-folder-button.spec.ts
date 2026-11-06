import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { signIn, view } from './support';

/** A made-up one-page drawing as a PDF; nothing here is a real part or drawing. */
async function pdf(browser: Browser, title: string) {
  const p = await browser.newPage();
  await p.setContent(`<html><body style="font-family:Arial;border:3px solid #000;height:640px;padding:20px"><h1>${title}</h1><p>MADE-UP TEST DRAWING · NOT FOR USE</p></body></html>`);
  const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

/** Clicks Import folder the way the page does and chooses a directory in the browser folder picker. */
async function chooseFolder(page: Page, dir: string) {
  const box = view(page).locator('[data-upload-surface="parts-drawings"]');
  const button = box.getByRole('button', { name: 'Import folder' });
  const plan = page.waitForRequest((r) => r.method() === 'POST' && r.url().includes('/api/drawings/import/plan'));
  const [chooser] = await Promise.all([
    page.waitForEvent('filechooser'),
    button.click(),
  ]);
  await chooser.setFiles(dir);
  return plan;
}

test('Import folder sends the upload, opens again, and a second pick does not duplicate drawings', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-folder-btn-'));
  const top = path.join(root, `Example drive ${k}`);
  const pumps = `Pumps ${k}`;
  fs.mkdirSync(path.join(top, pumps, 'Housings'), { recursive: true });
  fs.writeFileSync(path.join(top, pumps, 'Housings', 'Housing plate.pdf'), await pdf(browser, 'HOUSING PLATE'));
  fs.writeFileSync(path.join(top, pumps, 'cover.pdf'), await pdf(browser, 'COVER'));
  fs.writeFileSync(path.join(top, 'readme.txt'), 'made-up notes');

  await page.goto('/sequfloh/drawings');
  const plan = await chooseFolder(page, top);
  expect(plan.method()).toBe('POST');
  expect(plan.url()).toContain('/api/drawings/import/plan');
  const dlg = page.getByRole('dialog');
  const preview = dlg.getByTestId('import-preview');
  await expect(preview).toContainText('To import2');
  await expect(preview).toContainText('PDF files2');
  await dlg.getByRole('button', { name: 'Import 2 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 2 of 2 files', { timeout: 60_000 });
  await dlg.getByRole('button', { name: 'Close' }).click();

  await v.getByRole('button', { name: pumps, exact: true }).click();
  await expect(v.getByRole('row').filter({ hasText: 'Housings' })).toContainText('1 drawing');
  await v.getByRole('button', { name: 'Housings', exact: true }).click();
  await expect(v.getByText('Housing plate', { exact: true })).toBeVisible();

  // back to All folders and pick the same drive again: the folders and drawings already here are reused
  await page.goto('/sequfloh/drawings');
  await chooseFolder(page, top);
  await expect(preview).toContainText('Already imported2');
  await expect(preview).toContainText('To import0');
  await expect(dlg.getByRole('button', { name: 'Import 0 drawings' })).toBeDisabled();
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  // the folder picker still opens after that
  const [again] = await Promise.all([
    page.waitForEvent('filechooser'),
    view(page).locator('[data-upload-surface="parts-drawings"]').getByRole('button', { name: 'Import folder' }).click(),
  ]);
  await again.setFiles(top);
  await expect(page.getByRole('dialog').getByTestId('import-preview')).toContainText('To import0');
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  await page.goto('/sequfloh/drawings');
  await v.getByRole('button', { name: pumps, exact: true }).click();
  await expect(v.getByRole('row').filter({ hasText: 'Housings' })).toContainText('1 drawing');
  fs.rmSync(root, { recursive: true, force: true });
});

test('a folder read that cannot reach the server shows an error and Import folder still opens', async ({ page, browser }) => {
  test.setTimeout(120_000);
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-folder-fail-'));
  const top = path.join(root, `Example fail ${k}`);
  fs.mkdirSync(path.join(top, 'Nested'), { recursive: true });
  fs.writeFileSync(path.join(top, 'Nested', 'plate.pdf'), await pdf(browser, 'PLATE'));

  await page.route('**/api/drawings/import/plan', (route) => route.abort());
  await page.goto('/sequfloh/drawings');
  await chooseFolder(page, top);
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByRole('alert')).toContainText('Nothing was uploaded');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  const sent = page.waitForRequest((r) => r.method() === 'POST' && r.url().includes('/api/drawings/import/plan'));
  const [chooser] = await Promise.all([
    page.waitForEvent('filechooser'),
    view(page).locator('[data-upload-surface="parts-drawings"]').getByRole('button', { name: 'Import folder' }).click(),
  ]);
  await chooser.setFiles(top);
  await sent;
  await expect(page.getByRole('dialog').getByRole('alert')).toContainText('Nothing was uploaded');
  fs.rmSync(root, { recursive: true, force: true });
});
