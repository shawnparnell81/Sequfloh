import path from 'node:path';
import { expect, test } from '@playwright/test';
import { choosePartName, choosePartType, settledShot, signIn, view } from './support';

/** Part form and Parts import offer set choices for Name, Product family, Part class, Unit of measure, Assembly, Lot controlled and Repairable. Synthetic data only. */
test('part choices: form dropdowns and import "same for all rows" or "from file column"', async ({ page }) => {
  const shots = process.env.E2E_PC_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);

  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'New part' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByLabel('OR # (Part number)').fill(`PC-${k}-1`);
  await choosePartType(page, 'Fuel Pump');
  await choosePartName(page, 'Fuel Pump');
  const pick = async (label: RegExp, option: string) => { await dlg.getByRole('combobox', { name: label }).click(); await page.getByRole('option', { name: option, exact: true }).click(); };
  await dlg.getByRole('combobox', { name: /^Part class/ }).click();
  for (const o of ['Complete Strut Assembly (CSA)', 'Coil Spring', 'Struts', 'Air Struts']) await expect(page.getByRole('option', { name: o, exact: true })).toBeVisible();
  await shot('01-part-form-part-class-open');
  await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await dlg.getByRole('combobox', { name: /^Unit of measure/ }).click();
  for (const o of ['Each', 'Lot', 'Batch', 'Skid']) await expect(page.getByRole('option', { name: o, exact: true })).toBeVisible();
  await page.getByRole('option', { name: 'Batch', exact: true }).click();
  await pick(/^Assembly/, 'Yes');
  await dlg.getByRole('combobox', { name: /^Lot controlled/ }).click();
  for (const o of ['Yes', 'No', 'Not applicable']) await expect(page.getByRole('option', { name: o, exact: true })).toBeVisible();
  await page.getByRole('option', { name: 'Not applicable', exact: true }).click();
  await pick(/^Repairable/, 'Yes');
  const site = dlg.getByLabel('Site');
  if (!(await site.textContent())?.trim().replace('\u200b', '')) { await site.click(); await page.getByRole('option').first().click(); }
  await shot('02-part-form-filled');
  await dlg.getByRole('button', { name: 'Save' }).click();
  await expect(dlg).toBeHidden();
  await expect(view(page).getByText('Not applicable')).toBeVisible();
  await shot('03-part-details');
  // Repairable does not put the part in Repair Parts
  await page.goto(`/sequfloh/parts?view=repair-parts&q=PC-${k}`);
  await expect(view(page).getByText(`PC-${k}-1`)).toHaveCount(0);

  const csv = ['OR #,SENSEN #,UOM,Lot,Class,Position',
    `PC-${k}-2,9200-${k}1,each,No,coil spring,Front`,
    `PC-${k}-3,9200-${k}2,SKID,n/a,Struts,Rear`,
    `PC-${k}-4,9200-${k}3,Pallet,No,Struts,Rear`].join('\n');
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-choices.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await expect(imp.getByText('For all rows (used where the file has no value)')).toHaveCount(0);
  await imp.getByRole('combobox', { name: /^Product family/ }).click();
  await expect(page.getByRole('option', { name: 'Not imported' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Same for all rows' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'From file column' })).toBeVisible();
  await shot('04-import-family-dropdown-open');
  await page.getByRole('option', { name: 'Complete Strut Assembly (CSA)', exact: true }).click();
  await expect(imp.locator('input[data-field="typeName"]')).toHaveValue('fix:Complete Strut Assembly (CSA)');
  // Name is required, only offers the family list and never a file column
  await expect(imp.getByRole('button', { name: 'Preview' })).toBeDisabled();
  await expect(imp.getByTestId('name-required')).toContainText('Choose the Name for all rows');
  await shot('08-import-name-required');
  await imp.getByRole('combobox', { name: /^Name/ }).click();
  await expect(page.getByRole('option', { name: 'Same for all rows' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'From file column' })).toHaveCount(0);
  await expect(page.getByRole('option', { name: 'Not imported' })).toHaveCount(0);
  await expect(page.getByRole('option', { name: 'OR #', exact: true })).toHaveCount(0);
  await shot('07-import-name-dropdown-open');
  await page.getByRole('option', { name: 'Complete Strut Assembly (CSA)', exact: true }).click();
  await expect(imp.getByTestId('name-required')).toHaveCount(0);
  await imp.getByRole('combobox', { name: /^Part class/ }).click();
  await page.getByRole('option', { name: 'Class', exact: true }).click();
  await imp.getByRole('combobox', { name: /^Assembly/ }).click();
  await page.getByRole('option', { name: 'Yes', exact: true }).click();
  await expect(imp.locator('input[data-field="unitOfMeasure"]')).toHaveValue('col:2');
  await expect(imp.locator('input[data-field="isLotControlled"]')).toHaveValue('col:3');
  await shot('05-import-mapping');
  await imp.getByRole('button', { name: 'Preview' }).click();
  const counts = imp.getByTestId('import-counts');
  await expect(counts).toContainText('Add 2'); await expect(counts).toContainText('Errors 1');
  await expect(imp.getByText('Unit of measure "Pallet" must be Each, Lot, Batch or Skid.')).toBeVisible();
  await shot('06-import-preview');
  await imp.getByRole('button', { name: 'Import 2 parts' }).click();
  await expect(imp.getByTestId('import-done')).toContainText('2 added');
  await imp.getByRole('button', { name: 'Close' }).click();
  await page.goto(`/sequfloh/parts?view=assemblies&q=PC-${k}`);
  await expect(view(page).getByText(`PC-${k}-2`)).toBeVisible();
  await expect(view(page).getByText(`PC-${k}-3`)).toBeVisible();
});
