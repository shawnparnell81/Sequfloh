import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Pass / Fail per part on a Test work order: automatic from the spec limits, and an override with a reason. */
test('test results: automatic pass and fail, override with a reason', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const pump = (await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; code: string }[]).find((f) => f.code === 'FUEL_PUMP')!;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `PF-${k}`, name: 'Example in-tank fuel pump', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId: pump.id } }))).id as string;
  const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Test', number: `E2E-PF-${k}`, siteId: site, familyId: pump.id, partId: part } }))).id as string;
  await ok(rq.put(`/api/sequfloh/tracking/${id}/test/spec`, { data: { values: [{ key: 'shutoffPressure', value: '320-360', unit: 'psi' }, { key: 'flowRate', value: '≥95', unit: 'L/h' }] } }));
  await ok(rq.post(`/api/sequfloh/tracking/${id}/test/parts`, { data: { values: [{ key: 'shutoffPressure', value: '340' }, { key: 'flowRate', value: '97' }] } }));
  await ok(rq.post(`/api/sequfloh/tracking/${id}/test/parts`, { data: { values: [{ key: 'shutoffPressure', value: '372' }, { key: 'flowRate', value: '99' }] } }));
  await page.goto(`/sequfloh/record/${id}`);
  const panel = view(page).getByTestId('test-results');
  await expect(panel.getByTestId('part-1-result')).toHaveText('Pass');
  await expect(panel.getByTestId('part-2-result')).toHaveText('Fail');
  await expect(panel.getByTestId('test-overall')).toHaveText('Fail');
  await panel.getByTestId('test-part-1').scrollIntoViewIfNeeded();
  await shot('01-auto-pass-fail');
  await panel.getByTestId('part-2-result-select').click();
  await page.getByRole('option', { name: 'Pass', exact: true }).click();
  await expect(panel.getByTestId('part-2-result-save')).toBeDisabled();
  await panel.getByTestId('part-2-result-reason').fill('Made-up gauge was out of calibration; retested on a calibrated gauge');
  await panel.getByTestId('part-2-result-save').click();
  await expect(panel.getByTestId('part-2-result')).toHaveText('Pass');
  await expect(panel.getByTestId('test-overall')).toHaveText('Pass');
  await expect(panel.getByTestId('part-2-passfail')).toContainText('automatic result Fail');
  await panel.getByTestId('test-part-2').scrollIntoViewIfNeeded();
  await shot('02-override-with-reason');
});
