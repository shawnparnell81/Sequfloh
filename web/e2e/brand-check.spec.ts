import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

test('brand dropdown, detail, and reference list', async ({ page }) => {
  await signIn(page);
  const v = view(page);
  await page.goto('/sequfloh/admin/reference');
  await expect(v.getByRole('link', { name: 'Brands' })).toBeVisible();
  await v.getByRole('link', { name: 'Brands' }).click();
  await expect(v.getByRole('cell', { name: 'Duralast', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'Spectra Premium', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'UAS', exact: true }).first()).toBeVisible();
  await expect(v.getByRole('cell', { name: 'WD Fuelmaster', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'Calvert Racing', exact: true })).toBeVisible();
  await expect(v.getByRole('cell', { name: 'RideMaster', exact: true })).toBeVisible();

  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'New part' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByRole('combobox', { name: 'Brand' }).click();
  await expect(page.getByRole('option', { name: 'Duralast', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Monoshock', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'UAS', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'WD Fuelmaster', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Nissan', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Calvert Racing', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.keyboard.press('Escape');

  const rq = page.request;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const sites = await ok(rq.get('/api/organization/sites'));
  const families = await ok(rq.get('/api/sequfloh/reference/product-families'));
  const brandList = await ok(rq.get('/api/sequfloh/reference/brands'));
  const csa = families.find((f: { label: string }) => f.label === 'Complete Strut Assembly (CSA)').id as string;
  const ride = brandList.find((b: { label: string }) => b.label === 'RideMaster').id as string;
  const pn = `NB-${Date.now().toString(36).toUpperCase()}`;
  await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Example part ' + pn, description: 'plain description', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: sites[0].id, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: null, drawingRevision: null, specification: null },
    typeId: csa, brandId: ride, attributes: { keyApplication: 'kept application' },
  } }));
  await page.goto(`/sequfloh/parts?customer=unassigned&partType=${csa}`);
  await expect(v.getByTestId(`part-brand-${pn}`)).toHaveText('RideMaster');
  await v.getByRole('combobox', { name: 'Brand' }).click();
  await page.getByRole('option', { name: 'RideMaster', exact: true }).click();
  await expect(v.getByTestId(`part-brand-${pn}`)).toBeVisible();
  await v.getByRole('combobox', { name: 'Brand' }).click();
  await page.getByRole('option', { name: 'Duralast', exact: true }).click();
  await expect(v.getByTestId(`part-brand-${pn}`)).toHaveCount(0);
});
