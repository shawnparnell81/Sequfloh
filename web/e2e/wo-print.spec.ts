import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. The Print button on a work order opens the one-page work order print. */
test('work order print', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const fams = await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; code: string }[];
  const pump = fams.find((f) => f.code === 'FUEL_PUMP')!;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `PR-${k}`, name: 'Example in-tank fuel pump', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId: pump.id } }))).id as string;
  const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Test', number: `E2E-TST-PR-${k}`, siteId: site, familyId: pump.id, partId: part } }))).id as string;
  await ok(rq.put(`/api/sequfloh/tracking/${id}/test/spec`, { data: { values: [{ key: 'testPressure', value: '60', unit: 'psi' }, { key: 'shutoffPressure', value: '320-360', unit: 'psi' }, { key: 'flowRate', value: '≥95', unit: 'L/h' }, { key: 'amps', value: '<=6.0', unit: 'A' }] } }));
  for (const [a, b] of [['60', '338'], ['61', '341'], ['59', '352']]) await ok(rq.post(`/api/sequfloh/tracking/${id}/test/parts`, { data: { values: [{ key: 'testPressure', value: a }, { key: 'shutoffPressure', value: b }, { key: 'flowRate', value: '98' }, { key: 'amps', value: '5.2' }] } }));
  await ok(rq.post(`/api/sequfloh/tracking/${id}/job/notes`, { data: { text: 'Example: bench 2 used, fixture re-torqued before part 3.' } }));
  await ok(rq.post(`/api/sequfloh/tracking/${id}/job/notes`, { data: { text: 'Example: parts staged for final review.' } }));
  await page.goto(`/sequfloh/record/${id}`);
  await expect(view(page).getByTestId('wo-print')).toBeVisible();
  if (shots) await settledShot(page, path.join(shots, '01-work-order-print-button.png'));
  expect(await view(page).getByTestId('wo-print').getAttribute('href')).toContain('/print/work-order');
  const pdf = await rq.get(`/api/sequfloh/tracking/${id}/print/work-order`);
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  const full = await rq.get(`/api/sequfloh/tracking/${id}/print`);
  if (shots) { fs.writeFileSync(path.join(shots, 'work-order-print.pdf'), await pdf.body()); fs.writeFileSync(path.join(shots, 'full-record-print.pdf'), await full.body()); }
});
