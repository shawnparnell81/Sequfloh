import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Part Master bulk import: read a synthetic CSV, check the mapping, preview Add / Update / Skip / Error, then import. */
test('parts import: mapping, preview and confirm', async ({ page }) => {
  const shots = process.env.E2E_PI_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  const csv = ['Item Number,Description,Rev,Category,Repairable,Repair part,NetSuite Status',
    `EX-${k}-01,Example gearbox housing,A,Strut,Yes,No,`,
    `EX-${k}-02,Example screw M4 x 10,A,Gas Lift,No,Yes,`,
    `EX-${k}-03,Example hex nut M4,A,Gas Lift,No,Yes,`,
    `EX-${k}-03,Example hex nut M4 (repeat),A,Gas Lift,No,Yes,`,
    `EX-${k}-04,Example top mount,B,Air Strut,No,Yes,`,
    `,Row without a part number,A,Gas Lift,No,No,`,
    `EX-${k}-05,Example bracket,A,Fuel Pump,Maybe,No,`].join('\n');
  await signIn(page);
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-parts.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await expect(dlg.getByText('7 rows found')).toBeVisible();
  await expect(dlg.locator('input[data-field="partNumber"]')).toHaveValue('0');
  await expect(dlg.locator('input[data-field="isRepairPart"]')).toHaveValue('5');
  await expect(dlg.locator('input[data-field="statusTag"]')).toHaveValue('6');
  await shot('01-column-mapping');
  await dlg.getByRole('button', { name: 'Preview' }).click();
  const counts = dlg.getByTestId('import-counts');
  await expect(counts).toContainText('Add 4'); await expect(counts).toContainText('Skip 1'); await expect(counts).toContainText('Errors 2');
  await expect(dlg.getByText('Duplicate OR # (Part number)')).toBeVisible();
  await expect(dlg.getByText('Repairable must be Yes or No.')).toBeVisible();
  await shot('02-preview');
  await dlg.getByRole('button', { name: 'Import 4 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('4 added, 0 updated, 1 skipped, 2 rows with errors not imported');
  await shot('03-imported');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the same file again: existing parts are skipped by default, or updated when chosen
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-parts.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(counts).toContainText('Add 0'); await expect(counts).toContainText('Skip 5');
  await dlg.getByLabel('Update existing').check();
  await expect(counts).toContainText('Update 4');
  await shot('04-update-existing');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  await page.goto(`/sequfloh/parts?view=repair-parts&q=EX-${k}`);
  await expect(view(page).getByText(`EX-${k}-02`)).toBeVisible();
  await expect(view(page).getByText(`EX-${k}-01`)).toHaveCount(0);
  await page.goto('/admin/audit-trail');
  await expect(view(page).getByText(/Imported parts from example-parts.csv: 4 added/).first()).toBeVisible();
  await shot('05-audit-entry');
});
