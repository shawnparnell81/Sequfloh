import path from 'node:path';
import { expect, test } from '@playwright/test';
import { choosePartType, settledShot, signIn, view } from './support';

/** Part types from the admin list, the cross-reference fields, list columns and import detection. Made-up parts only. */
test('part types and cross-reference fields', async ({ page }) => {
  const shots = process.env.E2E_PT_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;

  // the admin list under Parts, seeded with the part types
  await page.goto('/sequfloh/admin/dropdowns?list=part-types');
  for (const t of ['Fuel Pump', 'Fuel Injector', 'CSA', 'Strut', 'Brake Wear Sensor', 'Air Strut', 'Gas Lift', 'Electric Lift'])
    await expect(view(page).getByRole('cell', { name: t, exact: true }).first()).toBeVisible();
  await shot('01-part-types-setup');

  // New part needs a part type; the cross-reference fields are on the form
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'New part' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByLabel('OR # (Part number)')).toBeVisible();
  await expect(dlg.getByLabel('SENSEN # (Dwg #)')).toBeVisible();
  await dlg.getByLabel('OR # (Part number)').fill(`OR-${k}`);
  await dlg.getByLabel('Name').fill('Example front strut');
  await dlg.getByLabel('SENSEN # (Dwg #)').fill(`SN-${k}`);
  const site_ = dlg.getByLabel('Site');
  if (!(await site_.textContent())?.trim().replace('\u200b', '')) { await site_.click(); await page.getByRole('option').first().click(); }
  await dlg.getByLabel('Nissan VA #').fill(`NVA-${k}`);
  await dlg.getByLabel('NAPA US #').fill(`NUS-${k}`);
  await dlg.getByLabel('NAPA CA #').fill(`NCA-${k}`);
  await dlg.getByLabel('Key Application').fill('Example sedan 2019-2023, front left');
  await expect(dlg.getByRole('button', { name: 'Save' })).toBeDisabled();
  await dlg.getByRole('combobox', { name: /^Part type/ }).click();
  await expect(page.getByRole('option', { name: 'Electric Lift' })).toBeVisible();
  await shot('02-new-part-type-dropdown');
  await page.getByRole('option', { name: 'Strut', exact: true }).click();
  await shot('03-new-part-filled');
  await dlg.getByRole('button', { name: 'Save' }).click();
  await expect(dlg).toBeHidden();
  await expect(view(page).getByText('Nissan VA #')).toBeVisible();
  await expect(view(page).getByText(`NVA-${k}`)).toBeVisible();
  await shot('04-part-details');

  // list: part type filter, search by a cross-reference number, column chooser
  await page.goto(`/sequfloh/parts?q=NUS-${k}`);
  await expect(view(page).getByText(`OR-${k}`)).toBeVisible();
  await expect(view(page).getByRole('columnheader', { name: 'Part type' })).toBeVisible();
  await expect(view(page).getByRole('columnheader', { name: 'NAPA CA #' })).toBeVisible();
  await shot('05-list-search-by-napa');
  await view(page).getByRole('button', { name: 'Columns' }).click();
  await page.getByRole('menuitem', { name: 'Key Application' }).click();
  await shot('06-column-chooser');
  await page.keyboard.press('Escape');
  await expect(view(page).getByRole('columnheader', { name: 'Key Application' })).toBeVisible();

  // a part without a type (added before part types) is valid and prompts for one
  const untyped = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `OLD-${k}`, name: 'Example older part', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false } } }))).id as string;
  await page.goto('/sequfloh/parts');
  await expect(view(page).getByTestId('untyped-parts')).toBeVisible();
  await shot('07-list-untyped-prompt');
  await page.goto(`/sequfloh/parts/${untyped}`);
  await expect(view(page).getByTestId('set-part-type')).toBeVisible();
  await view(page).getByRole('button', { name: 'Set part type' }).click();
  await choosePartType(page, 'Gas Lift');
  await shot('08-set-type-on-older-part');
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click();
  await expect(view(page).getByTestId('set-part-type')).toHaveCount(0);
  await expect(view(page).getByText('Gas Lift')).toBeVisible();

  // import detects OR#, SENSEN #, Part Type and the cross-reference headings
  const csv = ['OR#,SENSEN #,Description,Part Type,Nissan VA,NAPA US,NAPA CA,Key Application',
    `IM-${k}-1,SN-${k}-1,Example fuel pump module,Fuel Pump,NV-1,NU-1,NC-1,Example hatchback 2018-2022`,
    `IM-${k}-2,SN-${k}-2,Example gas lift support,Gas Lift,NV-2,NU-2,,Example wagon tailgate`].join('\n');
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  const imp = page.getByRole('dialog');
  await imp.getByTestId('import-file').setInputFiles({ name: 'example-cross-reference.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await imp.getByRole('button', { name: 'Read file' }).click();
  await expect(imp.locator('input[data-field="partNumber"]')).toHaveValue('0');
  await expect(imp.locator('input[data-field="drawingNumber"]')).toHaveValue('1');
  await expect(imp.locator('input[data-field="typeName"]')).toHaveValue('3');
  await expect(imp.locator('input[data-field="attr:napaCa"]')).toHaveValue('6');
  await shot('09-import-mapping');
  await imp.getByRole('button', { name: 'Preview' }).click();
  await expect(imp.getByTestId('import-counts')).toContainText('Add 2');
  await imp.getByRole('button', { name: 'Import 2 parts' }).click();
  await expect(imp.getByTestId('import-done')).toContainText('2 added');
  await imp.getByRole('button', { name: 'Close' }).click();
  await page.goto(`/sequfloh/parts?q=IM-${k}`);
  await expect(view(page).getByText(`IM-${k}-1`)).toBeVisible();
  await shot('10-imported-parts');
});
