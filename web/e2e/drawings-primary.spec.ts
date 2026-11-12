import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Browser } from '@playwright/test';
import { chooseImportScope, section, signIn, view } from './support';

/** A made-up one-page drawing as a PDF; nothing here is a real part or drawing. */
async function pdf(browser: Browser, title: string) {
  const p = await browser.newPage();
  await p.setContent(`<html><body style="font-family:Arial;border:3px solid #000;height:640px;padding:20px"><h1>${title}</h1><p>MADE-UP TEST DRAWING · NOT FOR USE</p></body></html>`);
  const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

test('drawings: a part folder with old and reference copies imports every file and links only the one named for the folder', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const k = Date.now().toString(36).toUpperCase();
  const pn = (n: number) => `PR${k}-${String(n).padStart(3, '0')}`;
  await signIn(page);
  const v = view(page);

  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-parts.csv', mimeType: 'text/csv', buffer: Buffer.from(`OR PN\n${pn(1)}\n${pn(2)}\n`) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await imp.getByRole('combobox', { name: /^Name/ }).click(); await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await chooseImportScope(page, 'Fuel Pump');
  await imp.getByRole('button', { name: 'Preview' }).click();
  await imp.getByRole('button', { name: 'Import 2 parts' }).click();
  await expect(imp.getByTestId('import-done')).toContainText('2 added', { timeout: 60_000 });
  await imp.getByRole('button', { name: 'Close' }).click();

  // like CSA Drawings\9212-0017 on the drive: the part's drawing, another part's drawing and an old copy in one folder
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-primary-'));
  const top = path.join(root, `Drive ${k}`);
  const dir = path.join(top, `Family ${k}`, pn(1));
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, `${pn(1)}.pdf`), await pdf(browser, 'CURRENT DRAWING'));
  fs.writeFileSync(path.join(dir, `${pn(2)}.pdf`), await pdf(browser, 'OTHER PART FOR REFERENCE'));
  fs.writeFileSync(path.join(dir, `${pn(1)} OLD.pdf`), await pdf(browser, 'OLD COPY OF THE DRAWING'));

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  await expect(dlg.getByTestId('import-preview')).toContainText('To import3');
  await dlg.getByRole('button', { name: 'Import 3 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 3 of 3 files', { timeout: 60_000 });
  await expect(dlg.getByTestId('import-progress')).toContainText('1 part links');
  await dlg.getByRole('button', { name: 'Close' }).click();

  const forPart = async (n: string) => (await (await page.request.get(`/api/drawings/for-part?partNumber=${encodeURIComponent(n)}`)).json() as { fileName: string }[]).map((x) => x.fileName);
  expect(await forPart(pn(1))).toEqual([`${pn(1)}.pdf`]);
  expect(await forPart(pn(2))).toEqual([]);
  const inventory = (await (await page.request.get('/api/drawings/import/inventory')).json() as { folder: string; fileName: string }[]).filter((x) => x.folder === `Family ${k}/${pn(1)}`);
  expect(inventory.map((x) => x.fileName).sort()).toEqual([`${pn(1)} OLD.pdf`, `${pn(1)}.pdf`, `${pn(2)}.pdf`]);
});
