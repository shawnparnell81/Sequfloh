import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Part Master import of every tab of a made-up workbook: five product family tabs, one tab that is no family. */
test('parts import: every tab, tab name is the product family', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  await signIn(page);
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('import-file').setInputFiles(path.join(import.meta.dirname, 'example-part-families.xlsx'));
  await dlg.getByLabel('Import every tab: tab name is the product family').check();
  await dlg.getByRole('button', { name: 'Read file' }).click();
  const summary = dlg.getByTestId('tabs-summary');
  await expect(summary).toContainText('5 tabs to import · 9 rows');
  await expect(summary).toContainText('Complete Strut Assembly (CSA)');
  await expect(summary).toContainText('Tab "Notes" was skipped: it does not match a product family.');
  await shot('01-tabs-read');
  await dlg.getByLabel('Fill in blanks of existing').check();
  await dlg.getByRole('button', { name: 'Preview' }).click();
  const counts = dlg.getByTestId('tab-counts');
  await expect(counts).toContainText('Fuel Injector');
  await expect(counts).toContainText('Gas Lift');
  await expect(dlg.getByTestId('tab-skipped')).toContainText('Notes');
  await expect(dlg.getByText(/Tab Fuel Pump, row 5/).first()).toBeVisible(); // the repeat across tabs points at its tab and row
  await expect(dlg.getByText('Tab Coil Spring, row 4')).toBeVisible();
  await shot('02-preview');
  await dlg.getByRole('button', { name: /^Import \d+ parts$/ }).click();
  await expect(dlg.getByTestId('import-done')).toBeVisible();
  await shot('03-imported');
});
