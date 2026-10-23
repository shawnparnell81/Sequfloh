import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Part Master home: search, one card per product family with counts, and a family's parts list with its own import. */
test('parts home: search, family cards and fuel pump import', async ({ page }) => {
  const shots = process.env.E2E_PH_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  const families = ['Complete Strut Assembly (CSA)', 'Fuel Pump', 'Fuel Injector', 'Brake Wear Sensor', 'Gas Lift', 'Electric Lift', 'Coil Spring', 'Struts', 'Air Struts'];
  await signIn(page);
  await page.goto('/sequfloh/parts');
  const v = view(page);
  for (const f of families) await expect(v.getByTestId(`family-card-${f}`)).toBeVisible();
  await expect(v.getByRole('button', { name: 'Import parts' })).toBeVisible();
  await expect(v.getByRole('button', { name: 'New part' })).toBeVisible();
  await expect(v.getByRole('columnheader', { name: 'Updated' })).toHaveCount(0);
  const card = v.getByTestId('family-card-Fuel Pump');
  await expect(card.getByTestId('family-count')).not.toHaveText('…');
  const before = Number(await card.getByTestId('family-count').innerText());

  // import a made-up fuel pump file from the Fuel Pump page: Name and family are preset
  await card.click();
  await expect(v.getByRole('heading', { name: 'Fuel Pump' }).first()).toBeVisible();
  await v.getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  const csv = ['OR#,SENSEN#,NAPA US #,Key Application',
    `FP-${k}-1,SF-${k}-1,NU-${k}-1,2001-2005 Example Wagon`,
    `FP-${k}-2,SF-${k}-2,NU-${k}-2,2010-2014 Example Van`,
    `FP-${k}-3,SF-${k}-3,NU-${k}-3,1999-2003 Example Truck`].join('\n');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-fuel-pumps.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await expect(dlg.locator('input[data-field="name"]')).toHaveValue('fix:Fuel Pump');
  await expect(dlg.locator('input[data-field="typeName"]')).toHaveValue('fix:Fuel Pump');
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(dlg.getByTestId('import-counts')).toContainText('Add 3');
  await dlg.getByRole('button', { name: 'Import 3 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('3 added');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the family list keeps filters, Columns and Export CSV
  await v.getByLabel('Search').first().fill(`FP-${k}`); await v.getByLabel('Search').first().press('Enter');
  await expect(v.getByText(`FP-${k}-3`)).toBeVisible();
  await expect(v.getByRole('button', { name: 'Columns' })).toBeVisible();
  await expect(v.getByRole('button', { name: 'Export CSV' })).toBeVisible();
  await shot('11-family-parts-list');

  await v.getByTestId('parts-home-link').click();
  await expect(card.getByTestId('family-count')).toHaveText(String(before + 3));
  await shot('09-parts-home');

  // search by NAPA # and open the part
  await v.getByTestId('parts-search').fill(`NU-${k}-2`);
  await v.getByRole('button', { name: 'Search', exact: true }).click();
  const res = v.getByTestId('parts-search-results');
  await expect(res.getByText(`FP-${k}-2`)).toBeVisible();
  await expect(res.getByText(`FP-${k}-1`)).toHaveCount(0);
  await shot('10-parts-search');
  await res.getByText(`FP-${k}-2`).click();
  await expect(page).toHaveURL(/\/sequfloh\/parts\/[0-9a-f-]{36}/);
});
