import path from 'node:path';
import { expect, test } from '@playwright/test';
import { chooseImportScope, settledShot, signIn, view } from './support';

/** Part Master home: one card per customer, then that customer's product lines. */
test('parts home: customer cards and a product line import', async ({ page }) => {
  const shots = process.env.E2E_PH_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  await page.goto('/sequfloh/parts');
  const v = view(page);
  for (const c of ['NAPA US', 'NAPA Canada', "O'Reilly", 'AutoZone', 'Unassigned']) await expect(v.getByTestId(`customer-card-${c}`)).toBeVisible();
  await expect(v.getByTestId('customer-card-SenSen')).toHaveCount(0);
  await expect(v.getByRole('button', { name: 'Import parts' })).toBeVisible();
  await expect(v.getByRole('button', { name: 'New part' })).toBeVisible();
  const card = v.getByTestId("customer-card-O'Reilly");
  await expect(card.getByTestId('customer-count')).not.toHaveText('…');
  const before = Number(await card.getByTestId('customer-count').innerText());

  await v.getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  const csv = ['OR#,SENSEN#,NAPA US #,Key Application',
    `FP-${k}-1,SF-${k}-1,NU-${k}-1,2001-2005 Example Wagon`,
    `FP-${k}-2,SF-${k}-2,NU-${k}-2,2010-2014 Example Van`,
    `FP-${k}-3,SF-${k}-3,NU-${k}-3,1999-2003 Example Truck`].join('\n');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-fuel-pumps.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await dlg.getByRole('combobox', { name: /^Name/ }).click();
  await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await dlg.getByRole('combobox', { name: /^Product family/ }).click();
  await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await chooseImportScope(page, 'Fuel Pump');
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(dlg.getByTestId('import-counts')).toContainText('Add 3');
  await dlg.getByRole('button', { name: 'Import 3 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('3 added');
  await dlg.getByRole('button', { name: 'Close' }).click();

  await expect(card.getByTestId('customer-count')).toHaveText(String(before + 3));
  await card.locator('a').click();
  await v.getByTestId('line-tab-Fuel Pump').click();
  await expect(v.getByRole('columnheader', { name: 'Customer part #' })).toBeVisible();
  await expect(v.getByRole('columnheader', { name: 'Also sold as' })).toBeVisible();
  await v.getByLabel('Search').first().fill(`FP-${k}`); await v.getByLabel('Search').first().press('Enter');
  await expect(v.locator('span').filter({ hasText: new RegExp(`^FP-${k}-3$`) })).toBeVisible();
  await expect(v.getByRole('button', { name: 'Export CSV' })).toBeVisible();
  await shot('11-customer-line-list');

  await v.getByTestId('parts-home-link').click();
  await shot('09-parts-home');

  // a NAPA alias is not the part number, so the matches are shown and not opened on their own
  await v.getByTestId('parts-search').fill(`NU-${k}-2`);
  await v.getByRole('button', { name: 'Search', exact: true }).click();
  const res = v.getByTestId('parts-search-results');
  await expect(res.getByText(`FP-${k}-2`)).toBeVisible();
  await expect(res.getByText(`FP-${k}-1`)).toHaveCount(0);
  await shot('10-parts-search');
  await res.getByTestId('search-hit').filter({ hasText: `FP-${k}-2` }).click();
  await expect(v.getByTestId('found-part')).toContainText(`FP-${k}-2`);
  await v.getByTestId('found-part').click();
  await expect(page).toHaveURL(/\/sequfloh\/parts\/[0-9a-f-]{36}/);
});
