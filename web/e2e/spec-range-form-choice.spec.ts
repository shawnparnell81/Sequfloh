import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Weight "Multiple", the Test order validation form choice, and spec ranges and limits. */
test('weight multiple, test form choice, spec ranges', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();

  await page.goto('/sequfloh/tracking/new?type=Weight');
  { const n = page.getByTestId('create-clock-no'); await n.waitFor({ timeout: 5000 }).then(() => n.click(), () => {}); }
  await page.locator('div:has(> input[data-testid="new-wo-family"]) [role=combobox]').first().click();
  await expect(page.getByTestId('new-wo-family-multiple')).toBeVisible();
  await shot('01-weight-family-multiple-option');
  await page.getByTestId('new-wo-family-multiple').click();
  await shot('02-weight-multiple-selected');

  await page.goto('/sequfloh/tracking/new?type=Test');
  { const n = page.getByTestId('create-clock-no'); await n.waitFor({ timeout: 5000 }).then(() => n.click(), () => {}); }
  await expect(page.getByTestId('form-choice-no')).toBeVisible();
  await shot('03-test-create-form-choice-default-no');

  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const fams = await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; code: string }[];
  const pump = fams.find((f) => f.code === 'FUEL_PUMP')!;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `RG-${k}`, name: 'Example fuel pump', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId: pump.id } }))).id as string;
  const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Test', number: `E2E-TST-RG-${k}`, siteId: site, familyId: pump.id, partId: part } }))).id as string;
  await page.goto(`/sequfloh/record/${id}`);
  await view(page).getByTestId('wo-form-choice').scrollIntoViewIfNeeded();
  await shot('04-test-order-form-choice-no');
  await view(page).getByTestId('form-choice-yes').click();
  await expect(view(page).getByTestId('wo-form-choice')).toBeVisible();
  await page.waitForTimeout(800);
  await view(page).getByTestId('wo-form-choice').scrollIntoViewIfNeeded();
  await shot('05-test-order-form-choice-yes');

  await ok(rq.put(`/api/sequfloh/tracking/${id}/test/spec`, { data: { values: [
    { key: 'testPressure', value: '60', unit: 'psi' }, { key: 'shutoffPressure', value: '320-360', unit: 'psi' },
    { key: 'flowRate', value: '≥95', unit: 'L/h' }, { key: 'amps', value: '<=6.0', unit: 'A' }] } }));
  await ok(rq.post(`/api/sequfloh/tracking/${id}/test/parts`, { data: { values: [{ key: 'testPressure', value: '60' }, { key: 'shutoffPressure', value: '370' }, { key: 'flowRate', value: '97' }, { key: 'amps', value: '6.4' }] } }));
  await page.goto(`/sequfloh/record/${id}`);
  await expect(view(page).getByTestId('part-1-shutoffPressure-outside')).toBeVisible();
  await expect(view(page).getByTestId('part-1-amps-outside')).toBeVisible();
  await expect(view(page).getByTestId('part-1-flowRate-outside')).toHaveCount(0);
  await expect(view(page).getByTestId('spec-shutoffPressure')).toHaveValue('320-360');
  await view(page).getByTestId('test-results').scrollIntoViewIfNeeded();
  await shot('06-spec-range-and-limits-outside-flag');
});
