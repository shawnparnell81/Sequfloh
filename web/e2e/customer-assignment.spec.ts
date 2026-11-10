import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Customer assignment on existing parts. The customer tabs and the new import are not part of this screen. */
test('assign a clear customer and leave an ambiguous part unassigned', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const v = view(page);

  await page.goto('/sequfloh/parts');
  await expect(v.getByTestId('assign-customers')).toBeVisible();
  await shot('01-part-master-assign-button');

  const sites = await (await page.request.get('/api/organization/sites')).json();
  const families = await (await page.request.get('/api/sequfloh/reference/product-families')).json();
  const csa = families.find((f: { label: string }) => f.label === 'Complete Strut Assembly (CSA)').id as string;
  const site = sites[0].id as string;
  const part = (pn: string, attributes: Record<string, string>, drawing: string) => page.request.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Example part ' + pn, description: 'Example strut', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: drawing, drawingRevision: 'A', specification: null },
    typeId: csa, attributes,
  } });
  const clearPn = `OR-${k}`;
  const ambiguousPn = `BOTH-${k}`;
  const drawing = `9214-0800-${k}`;
  const clear = await part(clearPn, { orNumber: clearPn, napaUs: `NU-${k}`, position: 'FL' }, drawing);
  expect(clear.ok(), await clear.text()).toBeTruthy();
  const clearId = (await clear.json()).id as string;
  const ambiguous = await part(ambiguousPn, { orNumber: ambiguousPn, napaUs: ambiguousPn, position: 'FR' }, drawing);
  expect(ambiguous.ok(), await ambiguous.text()).toBeTruthy();

  await page.goto(`/sequfloh/parts/${clearId}`);
  await expect(v.getByTestId('part-customer')).toHaveText('Unassigned');
  await shot('02-part-detail-unassigned');

  await page.goto('/sequfloh/parts');
  await v.getByTestId('assign-customers').click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByText('Only the customer is saved.')).toBeVisible();
  await dlg.getByTestId('assign-search').fill(k);
  await dlg.getByRole('button', { name: 'Search' }).click();
  await expect(dlg.getByText(clearPn)).toBeVisible();
  await expect(dlg.getByText(ambiguousPn)).toBeVisible();
  await expect(dlg.getByRole('row', { name: new RegExp(clearPn) })).toContainText("O'Reilly");
  await expect(dlg.getByRole('row', { name: new RegExp(ambiguousPn) })).toContainText('Ambiguous');
  await expect(dlg.getByTestId('assign-table')).not.toContainText(/warning/i);
  await expect(dlg.getByTestId('assign-confirm')).toBeEnabled();
  await shot('03-assign-customers-preview');

  await dlg.getByTestId('assign-confirm').click();
  await expect(dlg.getByTestId('assign-done')).toContainText('assigned');
  await shot('04-assign-customers-confirmed');
  await dlg.getByRole('button', { name: 'Close' }).click();

  await page.goto(`/sequfloh/parts/${clearId}`);
  await expect(v.getByTestId('part-customer')).toHaveText("O'Reilly");
  await expect(v.getByText(drawing)).toBeVisible();
  await shot('05-part-detail-assigned');

  await page.goto('/sequfloh/admin/reference');
  const customersLink = v.getByRole('link', { name: 'Customers' });
  await customersLink.scrollIntoViewIfNeeded();
  await expect(customersLink).toBeVisible();
  await shot('06-reference-customers');
  await v.getByRole('link', { name: 'Customers' }).click();
  await expect(v.getByRole('cell', { name: "O'Reilly", exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'AutoZone', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'NAPA US', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'NAPA Canada', exact: true })).toBeVisible();
  await shot('07-dropdown-customers');
});
