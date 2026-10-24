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

test('drawings: import at the top level keeps the folder tree, PDFs only; the part picker finds any part by any number', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const shots = process.env.E2E_DF_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  const pn = (n: number) => `DF${k}-${String(n).padStart(3, '0')}`;
  await signIn(page);
  const v = view(page);

  // 130 made-up fuel pumps, each with its own NAPA and DMA numbers
  const rows = Array.from({ length: 130 }, (_, n) => `${pn(n)},NP${k}${n},DM${k}${n}`);
  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-parts.csv', mimeType: 'text/csv', buffer: Buffer.from(`OR PN,NAPA PN,DMA PN\n${rows.join('\n')}\n`) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await imp.getByRole('combobox', { name: /^Name/ }).click(); await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await imp.getByRole('button', { name: 'Preview' }).click();
  await imp.getByRole('button', { name: 'Import 130 parts' }).click();
  await expect(imp.getByTestId('import-done')).toContainText('130 added', { timeout: 60_000 });
  await imp.getByRole('button', { name: 'Close' }).click();

  // a made-up folder: Line A/<part>/, Line A/Fixtures/, a loose PDF, and files that are not PDFs
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'drawings-top-'));
  const top = path.join(root, `Example top ${k}`);
  fs.mkdirSync(path.join(top, `Line A ${k}`, pn(7)), { recursive: true }); fs.mkdirSync(path.join(top, `Line A ${k}`, 'Fixtures'), { recursive: true });
  fs.writeFileSync(path.join(top, `Line A ${k}`, pn(7), 'Pump assembly.pdf'), await pdf(browser, 'PUMP ASSEMBLY'));
  fs.writeFileSync(path.join(top, `Line A ${k}`, 'Fixtures', 'Fixture plate.pdf'), await pdf(browser, 'FIXTURE PLATE'));
  fs.writeFileSync(path.join(top, `Line A ${k}`, 'Fixtures', 'Fixture plate.dxf'), '0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n');
  fs.writeFileSync(path.join(top, 'Loose sheet.pdf'), await pdf(browser, 'LOOSE SHEET'));
  fs.writeFileSync(path.join(top, 'readme.txt'), 'made-up notes');

  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'Import drawings' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByRole('button', { name: 'Choose files' })).toHaveCount(0);
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  const preview = dlg.getByTestId('import-preview');
  await expect(preview).toContainText('Folders4 (4 new)');
  await expect(preview).toContainText('Files5');
  await expect(preview).toContainText('PDF files3');
  await expect(preview).toContainText('Skipped (not PDF or Word)2');
  await expect(preview).toContainText('To import3');
  await expect(dlg.getByText('Import these too', { exact: false })).toHaveCount(0);
  await shot('01-top-level-import-preview');
  await dlg.getByRole('button', { name: 'Import 3 drawings' }).click();
  await expect(dlg.getByTestId('import-progress')).toContainText('Finished. 3 of 3 files', { timeout: 60_000 });
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the chosen folder's subfolder is a top-level folder now, with its tree; the part folder is linked to the part
  await v.getByRole('button', { name: `Line A ${k}` }).click();
  await expect(v.getByRole('row').filter({ hasText: 'Fixtures' })).toContainText('1 drawing');
  await v.getByRole('button', { name: pn(7) }).click();
  await expect(v.getByText(`Part ${pn(7)}`)).toBeVisible();
  await expect(v.getByText('Pump assembly', { exact: true })).toBeVisible();
  await shot('02-imported-tree');

  // choosing the same folder again imports nothing new
  await section(page, 'Parts Drawings');
  await v.getByRole('button', { name: 'All folders' }).first().click();
  await v.getByRole('button', { name: 'Import drawings' }).click();
  await dlg.getByTestId('import-folder-input').setInputFiles(top);
  await expect(preview).toContainText('To import0');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  // the linked part picker finds part 125 of 130 by its DMA number, and part 118 by its NAPA number
  await v.getByRole('button', { name: 'New folder' }).click();
  const nf = page.getByRole('dialog');
  const picker = nf.getByLabel('Linked part (optional)');
  await picker.click();
  await expect(nf.getByText(/Showing \d+ of [\d,]+ parts/)).toBeVisible();
  await picker.fill(`DM${k}125`);
  await expect(page.getByRole('option')).toHaveCount(1);
  await expect(page.getByRole('option')).toContainText(pn(125));
  await shot('03-part-picker-dma');
  await picker.fill(`NP${k}118`);
  await expect(page.getByRole('option')).toContainText(pn(118));
  await page.getByRole('option').first().click();
  await expect(nf.getByText(pn(118))).toBeVisible();
  await shot('04-part-picker-chosen');
});
