import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** A made-up one-page drawing as a PDF; nothing here is a real part or drawing. */
async function pdf(browser: Browser, title: string) {
  const p = await browser.newPage();
  await p.setContent(`<html><body style="font-family:Arial;border:3px solid #000;height:640px;padding:20px"><h1>${title}</h1><p>MADE-UP TEST DRAWING · NOT FOR USE</p></body></html>`);
  const b = await p.pdf({ width: '1100px', height: '700px' }); await p.close(); return b;
}

async function importParts(page: Page, csv: string, count: number) {
  const v = view(page);
  await page.goto('/sequfloh/parts');
  await v.getByTestId('family-card-Fuel Pump').click(); // imported from the Fuel Pump page: the family is preset
  await v.getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-fuel-mixed.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await expect(imp.locator('input[data-field="typeName"]')).toHaveValue('fix:Fuel Pump');
  await imp.getByRole('button', { name: 'Preview' }).click();
  await imp.getByRole('button', { name: `Import ${count} parts` }).click();
  await expect(imp.getByTestId('import-done')).toContainText(`${count} added`, { timeout: 60_000 });
  await imp.getByRole('button', { name: 'Close' }).click();
}

test('parts: move the parts linked to a drawing folder to another product family, with a preview and the unmatched list', async ({ page, browser }) => {
  test.setTimeout(180_000);
  const shots = process.env.E2E_CHF_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);

  // made-up fuel pumps and injectors imported together under Fuel Pump; one has no drawing at all
  await importParts(page, `OR PN,NAPA PN,JH PN\nCF${k}-1,NP${k}1,INJ-A${k}\nCF${k}-2,NP${k}2,INJ-B${k}\nCF${k}-3,NP${k}3,PMP${k}\nCF${k}-4,NP${k}4,\n`, 4);

  // each chosen folder's subfolders become top-level drawing folders
  const injRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'change-family-i-'));
  const pumpRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'change-family-p-'));
  const inj = path.join(injRoot, `Fuel Injector Drawings ${k}`);
  fs.mkdirSync(path.join(inj, `INJ-B${k}`), { recursive: true });
  fs.writeFileSync(path.join(inj, `INJ-A${k}.pdf`), await pdf(browser, `INJ-A${k}`));
  fs.writeFileSync(path.join(inj, `INJ-B${k}`, `INJ-B${k}.pdf`), await pdf(browser, `INJ-B${k}`));
  const pump = path.join(pumpRoot, `Fuel Pump Drawings ${k}`);
  fs.mkdirSync(pump);
  fs.writeFileSync(path.join(pump, `PMP${k}.pdf`), await pdf(browser, `PMP${k}`));
  for (const [dir, n] of [[injRoot, 2], [pumpRoot, 1]] as const) {
    await section(page, 'Engineering');
    await page.getByRole('navigation', { name: 'Engineering pages' }).getByRole('link', { name: 'Drawings' }).click();
    await v.getByRole('button', { name: 'Import drawings' }).click();
    const dlg = page.getByRole('dialog');
    await dlg.getByTestId('import-folder-input').setInputFiles(dir);
    await dlg.getByRole('button', { name: `Import ${n} drawing${n === 1 ? '' : 's'}` }).click();
    await expect(dlg.getByTestId('import-progress')).toContainText('part link', { timeout: 60_000 });
    await dlg.getByRole('button', { name: 'Close' }).click();
  }

  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: /^Change product family/ }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByRole('tab', { name: 'Parts linked to drawings in folder' }).click();
  await dlg.getByRole('combobox', { name: 'Drawing folder' }).click();
  await page.getByRole('option', { name: `Fuel Injector Drawings ${k}` }).click();
  await dlg.getByRole('combobox', { name: 'Product family now' }).click(); await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await dlg.getByRole('combobox', { name: 'Move to product family' }).click(); await page.getByRole('option', { name: 'Fuel Injector', exact: true }).click();
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(dlg.getByTestId('cf-count')).toContainText('2 parts will move to Fuel Injector');
  const parts = dlg.getByTestId('cf-parts');
  await expect(parts.getByRole('row').filter({ hasText: `CF${k}-2` })).toContainText(`INJ-B${k}`); // drawing in a subfolder
  await expect(parts).not.toContainText(`CF${k}-3`);
  await expect(dlg.getByTestId('cf-unmatched')).toContainText(`CF${k}-4`);
  await expect(dlg.getByTestId('cf-unmatched')).not.toContainText(`CF${k}-3`);
  await shot('01-preview-folder-mode');
  const dl = page.waitForEvent('download');
  await dlg.getByRole('button', { name: 'Export to Excel' }).click();
  expect((await dl).suggestedFilename()).toBe('parts-without-drawings.xlsx');

  await dlg.getByRole('button', { name: 'Apply to 2 parts' }).click();
  await expect(dlg.getByTestId('cf-done')).toHaveText('Moved 2 parts to Fuel Injector.');
  await shot('02-applied');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the part shows its new family, keeps its fuel pump numbers, and the Fuel Injector card counts it
  await page.goto(`/sequfloh/parts?q=CF${k}-2`);
  await v.getByRole('row').filter({ hasText: `CF${k}-2` }).getByRole('cell').nth(1).click();
  await expect(v.getByText('Fuel Injector').first()).toBeVisible();
  await expect(v.getByText(`NP${k}2`)).toBeVisible();
  await shot('03-part-moved-keeps-numbers');
  await page.goto('/sequfloh/parts');
  await expect(v.getByText('Fuel Injector').first()).toBeVisible();
  await shot('04-family-cards');
});
