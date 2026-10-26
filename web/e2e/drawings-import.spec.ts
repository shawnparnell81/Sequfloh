import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Browser } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** A made-up one-page drawing as a PDF; nothing here is a real part or drawing. */
async function pdf(browser: Browser, title: string) {
  const p = await browser.newPage();
  await p.setContent(`<html><body style="font-family:Arial;border:3px solid #000;height:640px;padding:20px"><h1>${title}</h1><p>MADE-UP TEST DRAWING · NOT FOR USE</p></body></html>`);
  const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

test('drawings import: a folder tree with preview, part links, progress and a second run that skips what is there', async ({ page, browser }) => {
  const shots = process.env.E2E_DI_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  const part = `DI-${k}-PUMP`;
  await signIn(page);
  const v = view(page);

  // a made-up part whose part number is also a folder name in the drawings folder
  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-part.csv', mimeType: 'text/csv', buffer: Buffer.from(`OR PN\n${part}\n`) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await imp.getByRole('combobox', { name: /^Name/ }).click(); await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await imp.getByRole('button', { name: 'Preview' }).click();
  await imp.getByRole('button', { name: 'Import 1 part' }).click();
  await expect(imp.getByTestId('import-done')).toContainText('1 added');
  await imp.getByRole('button', { name: 'Close' }).click();

  // a made-up folder on disk: Pumps/<part>/ two PDFs, Pumps/Housings/ a PDF and a DXF, plus a text file that is not a drawing
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-import-'));
  const top = path.join(root, `Example drawings ${k}`);
  fs.mkdirSync(path.join(top, 'Pumps', part), { recursive: true }); fs.mkdirSync(path.join(top, 'Pumps', 'Housings'), { recursive: true });
  fs.writeFileSync(path.join(top, 'Pumps', part, 'Pump assembly.pdf'), await pdf(browser, 'PUMP ASSEMBLY'));
  fs.writeFileSync(path.join(top, 'Pumps', part, 'Pump strainer.pdf'), await pdf(browser, 'PUMP STRAINER'));
  fs.writeFileSync(path.join(top, 'Pumps', 'Housings', 'Housing.pdf'), await pdf(browser, 'HOUSING'));
  fs.writeFileSync(path.join(top, 'Pumps', 'Housings', 'Housing outline.dxf'), '0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n');
  fs.writeFileSync(path.join(top, 'readme.txt'), 'made-up notes');

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'New folder' }).click();
  await page.getByLabel('Folder name').fill(`Imports ${k}`);
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(v.getByText('This folder is empty.')).toBeVisible();

  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByLabel('Revision for all')).toHaveValue('A');
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  const preview = dlg.getByTestId('import-preview');
  await expect(preview).toContainText('Folders4 (4 new)');
  await expect(preview).toContainText('Files5');
  await expect(preview).toContainText('PDF files3');
  await expect(preview).toContainText('To import3');
  await expect(dlg.getByTestId('non-pdf')).toContainText('.dxf');
  await expect(dlg.getByText('not a drawing type and will be skipped')).toBeVisible();
  await expect(dlg.getByRole('row').filter({ hasText: `Pumps/${part}` }).first()).toContainText(part);
  await shot('01-import-preview');
  await dlg.getByLabel('Import these too (they download instead of opening in the browser)').check();
  await expect(preview).toContainText('To import4');
  await dlg.getByRole('button', { name: 'Import 4 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 4 of 4 files');
  await expect(dlg.getByTestId('import-progress')).toContainText('4 new folders');
  await shot('02-import-finished');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the tree is there; the part's folder is linked to the part
  await v.getByRole('button', { name: `Example drawings ${k}` }).click();
  await v.getByRole('button', { name: 'Pumps' }).click();
  await expect(v.getByRole('row').filter({ hasText: part })).toContainText('2 drawings');
  await v.getByRole('button', { name: part }).click();
  await expect(v.getByText(`Part ${part}`)).toBeVisible();
  await expect(v.getByText('Pump strainer', { exact: true })).toBeVisible();
  await shot('03-imported-folder');

  // picking the same folder again finds everything already imported
  await v.getByRole('button', { name: `Imports ${k}`, exact: true }).click();
  await v.getByRole('button', { name: 'Import drawings' }).click();
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  await expect(preview).toContainText('Already imported4');
  await expect(preview).toContainText('To import0');
  await expect(dlg.getByRole('button', { name: 'Import 0 drawings' })).toBeDisabled();
  await shot('04-second-run-skips');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  // one summary entry in the audit trail (kept on the Administration page, not on the folder)
  await expect(v.getByRole('button', { name: 'History' })).toHaveCount(0);
  await page.goto('/admin/audit-trail?q=Imported%20into%20drawing%20folder%20Imports');
  await expect(view(page).getByText(/Imported into drawing folder Imports .*: 4 drawings/).first()).toBeVisible();
  fs.rmSync(root, { recursive: true, force: true });
});
