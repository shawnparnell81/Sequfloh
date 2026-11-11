import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** A saved customer stays assigned. Choosing a customer on a row ticks it, and confirm refreshes the row. */
test('assigned parts leave the unassigned count and a chosen row is ticked', async ({ page }) => {
  const shots = '/opt/cursor/artifacts';
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const rq = page.request;
  const sites = await ok(rq.get('/api/organization/sites'));
  const families = await ok(rq.get('/api/sequfloh/reference/product-families'));
  const customers = await ok(rq.get('/api/sequfloh/reference/customers'));
  const csa = families.find((f: { label: string }) => f.label === 'Complete Strut Assembly (CSA)').id as string;
  const autozone = customers.find((c: { label: string }) => c.label === 'AutoZone').id as string;
  const site = sites[0].id as string;
  const make = (pn: string) => rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Example part ' + pn, description: 'Example strut', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: null, drawingRevision: null, specification: null },
    typeId: csa, attributes: {},
  } });
  const savedPn = `SAVED-${k}`;
  const openPn = `OPEN-${k}`;
  const saved = await ok(make(savedPn));
  await ok(make(openPn));
  await ok(rq.post('/api/sequfloh/parts/customers/assignment', { data: { assignClear: false, exceptions: [{ partId: saved.id, customerId: autozone }] } }));

  await page.goto('/sequfloh/parts');
  const v = view(page);
  await v.getByTestId('assign-customers').click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('assign-search').fill(k);
  await dlg.getByRole('button', { name: 'Search' }).click();
  await expect(dlg.getByTestId(`assign-suggestion-${savedPn}`)).toHaveText('Assigned: AutoZone');
  await expect(dlg.getByTestId('assign-counts')).toContainText('1 assigned');
  await expect(dlg.getByTestId(`assign-suggestion-${openPn}`)).toHaveText('Unassigned');
  await settledShot(page, path.join(shots, 'screenshot_assign_customers_assigned_row.png'));

  await dlg.getByTestId('assign-tab-Assigned').click();
  await expect(dlg.getByTestId(`assign-suggestion-${savedPn}`)).toBeVisible();
  await expect(dlg.getByTestId(`assign-suggestion-${openPn}`)).toHaveCount(0);
  await settledShot(page, path.join(shots, 'screenshot_assign_customers_tabs.png'));

  await dlg.getByTestId('assign-tab-all').click();
  await expect(dlg.getByTestId(`assign-include-${openPn}`)).not.toBeChecked();
  await dlg.getByRole('combobox', { name: `Customer for ${openPn}` }).click();
  await page.getByRole('option', { name: 'AutoZone', exact: true }).click();
  await expect(dlg.getByTestId(`assign-include-${openPn}`)).toBeChecked();
  await dlg.getByTestId('assign-confirm').click();
  await expect(dlg.getByTestId('assign-done')).toHaveText('1 assigned. Part numbers and links were not changed.');
  await expect(dlg.getByTestId(`assign-suggestion-${openPn}`)).toHaveText('Assigned: AutoZone');
  await settledShot(page, path.join(shots, 'screenshot_assign_customers_success.png'));

  await dlg.getByTestId('assign-tab-Unassigned').click();
  await expect(dlg.getByTestId(`assign-suggestion-${openPn}`)).toHaveCount(0);
  await expect(dlg.getByTestId(`assign-suggestion-${savedPn}`)).toHaveCount(0);
});
