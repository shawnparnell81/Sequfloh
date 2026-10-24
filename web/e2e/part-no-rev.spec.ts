import path from 'node:path';
import { expect, test } from '@playwright/test';
import { choosePartName, choosePartType, settledShot, signIn, view } from './support';

/** Parts have no revision; notes, editable OR #, Fuel Pump fields (JH part #, Box size) and part-number counts. */
test('parts: no revision, notes, editable OR # and fuel pump fields', async ({ page }) => {
  const shots = process.env.E2E_NR_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);

  // import made-up fuel pumps with the fuel pump sheet headers; no Revision field is offered
  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  const csv = ['OR PN,NAPA PN,DMA PN,Cross,JH PN,Individual Box Size',
    `NR-${k}-1,NU-${k}-1,DMA-${k}-1,CR-${k}-1,JH-${k}-1,10 x 8 x 6 in`,
    `NR-${k}-3,NU-${k}-3,DMA-${k}-3,CR-${k}-3,JH-${k}-2,10 x 8 x 6 in`,
    `NR-${k}-2,NU-${k}-2,DMA-${k}-2,CR-${k}-2,JH-${k}-3,12 x 10 x 8 in`].join('\n');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-fuel-pumps.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  const cols = { partNumber: 0, 'attr:napaPn': 1, 'attr:dmaPn': 2, 'attr:cross': 3, 'attr:jhPn': 4, 'attr:boxSize': 5 };
  for (const [f, i] of Object.entries(cols)) await expect(dlg.locator(`input[data-field="${f}"]`)).toHaveValue(`col:${i}`);
  await expect(dlg.locator('input[data-field="revision"]')).toHaveCount(0);
  await dlg.getByRole('combobox', { name: /^Name/ }).click();
  await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await dlg.getByRole('combobox', { name: /^Product family/ }).click();
  await page.getByRole('option', { name: 'Fuel Pump', exact: true }).click();
  await shot('04-import-mapping-no-rev');
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(dlg.getByTestId('import-counts')).toContainText('Add 3');
  await dlg.getByRole('button', { name: 'Import 3 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('3 added');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the Fuel Pump list shows JH part # and Box size and no Rev column
  await v.getByTestId('family-card-Fuel Pump').click();
  await v.getByRole('textbox', { name: 'Search', exact: true }).fill(`NR-${k}`); await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.getByText(`JH-${k}-3`)).toBeVisible();
  await expect(v.getByRole('columnheader', { name: 'JH PN', exact: true })).toBeVisible();
  await expect(v.getByRole('columnheader', { name: 'Individual Box Size' })).toBeVisible();
  // the first column is the tick boxes; poll because the table can re-render right after the search
  await expect.poll(async () => (await v.getByRole('columnheader').allInnerTexts()).map((h) => h.trim().toLowerCase()).filter(Boolean)).toEqual(['or pn', 'napa pn', 'dma pn', 'cross', 'jh pn', 'individual box size']);
  await expect(v.getByRole('columnheader', { name: 'Rev', exact: true })).toHaveCount(0);
  await shot('03-fuel-pump-list');

  // edit the OR # and add notes after import
  await v.getByText(`NR-${k}-2`).click();
  await expect(v.getByRole('tab', { name: 'Revisions' })).toHaveCount(0);
  await v.getByRole('button', { name: 'Edit' }).click();
  const ed = page.getByRole('dialog');
  await expect(ed.getByLabel('Revision')).toHaveCount(0);
  await expect(ed.getByLabel('JH PN')).toHaveValue(`JH-${k}-3`);
  await ed.getByLabel('OR PN').fill(`NR-${k}-2X`);
  await ed.getByTestId('part-notes-input').fill('Example note: ships with a new strainer.\nCheck the individual box size before packing.');
  await shot('01-part-form-notes');
  await ed.getByRole('button', { name: 'Save' }).click();
  await expect(ed).toBeHidden();
  await expect(v.getByTestId('part-notes')).toContainText('ships with a new strainer');
  await expect(v.getByText(`NR-${k}-2X`).first()).toBeVisible();
  await expect(v.getByText('Revision', { exact: true })).toHaveCount(0);
  await shot('02-part-details-notes');
  await expect(v.getByRole('tab', { name: 'History' })).toHaveCount(0);
  await page.goto(`/admin/audit-trail?q=NR-${k}-2X`);
  await expect(view(page).getByText(/NR-.*-2X/).first()).toBeVisible();

  // a new part form has no Revision and shows the Fuel Pump fields
  await page.goto('/sequfloh/parts');
  await expect(v.getByTestId('family-card-Fuel Pump')).toContainText('part numbers');
  await shot('05-home-part-numbers');
  await v.getByRole('button', { name: 'New part' }).click();
  await choosePartType(page, 'Fuel Pump');
  await choosePartName(page, 'Fuel Pump');
  await expect(page.getByRole('dialog').getByLabel('Revision')).toHaveCount(0);
  await expect(page.getByRole('dialog').getByLabel('Individual Box Size')).toBeVisible();
  await expect(page.getByRole('dialog').getByLabel('DWG #')).toHaveCount(0);
  await expect(page.getByRole('dialog').getByLabel('Position')).toHaveCount(0);
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();
});
