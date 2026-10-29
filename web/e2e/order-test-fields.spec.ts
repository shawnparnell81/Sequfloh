import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Shutoff pressure on fuel pump test orders, and a spec field added on one CSA test order. */
test('test orders: shutoff pressure and user-added spec fields', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const fams = await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; label: string; code: string }[];
  const pump = fams.find((f) => f.code === 'FUEL_PUMP')!; const csa = fams.find((f) => f.label.includes('CSA'))!;
  const mkPart = async (pn: string, name: string, typeId: string) => (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name, partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId } }))).id as string;
  const mkOrder = async (fam: string, part: string, n: string) => (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Test', number: n, siteId: site, familyId: fam, partId: part } }))).id as string;

  const fp = await mkOrder(pump.id, await mkPart(`FP-${k}`, 'Example in-tank fuel pump', pump.id), `E2E-TST-FP-${k}`);
  await ok(rq.post(`/api/sequfloh/tracking/${fp}/test/parts`, { data: { values: [{ key: 'testPressure', value: '59' }] } }));
  await page.goto(`/sequfloh/record/${fp}`);
  await expect(view(page).getByTestId('spec-shutoffPressure')).toBeVisible();
  await view(page).getByTestId('test-results').scrollIntoViewIfNeeded();
  await shot('01-fuel-pump-shutoff-pressure');

  const cs = await mkOrder(csa.id, await mkPart(`CSA-${k}`, 'Example strut assembly', csa.id), `E2E-TST-CSA-${k}`);
  await page.goto(`/sequfloh/record/${cs}`);
  await expect(view(page).getByTestId('test-no-fields')).toBeVisible();
  await view(page).getByTestId('test-add-field').click();
  await page.getByTestId('test-field-label').fill('Extended length');
  await page.getByTestId('test-field-unit').fill('in');
  await page.getByTestId('test-field-value').fill('12.5');
  await shot('02-add-spec-field');
  await page.getByTestId('test-field-save').click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await view(page).getByTestId('test-add-part').click();
  await expect(view(page).getByTestId('test-part-1')).toContainText('Extended length');
  await expect(view(page).getByTestId('test-part-1')).toContainText('12.5 in');
  await view(page).getByTestId('test-results').scrollIntoViewIfNeeded();
  await shot('03-csa-added-field-in-part-results');
});
