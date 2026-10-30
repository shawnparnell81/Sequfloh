import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. A 3-part Test work order: quantity on the Record tab, clocked time in the header, the lean job report with its two options. */
test('lean job report: quantity on the record tab, clocked time, one page with test results', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const pump = (await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; code: string }[]).find((f) => f.code === 'FUEL_PUMP')!;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `LR-${k}`, name: 'Example lean report pump', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId: pump.id } }))).id as string;
  const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Test', number: `E2E-LR-${k}`, siteId: site, familyId: pump.id, partId: part, quantity: 3 } }))).id as string;
  await ok(rq.put(`/api/sequfloh/tracking/${id}/test/spec`, { data: { values: [{ key: 'shutoffPressure', value: '320-360', unit: 'psi' }, { key: 'flowRate', value: '≥95', unit: 'L/h' }] } }));
  for (const [sp, fr] of [['340', '97'], ['350', '99'], ['372', '96']])
    await ok(rq.post(`/api/sequfloh/tracking/${id}/test/parts`, { data: { values: [{ key: 'shutoffPressure', value: sp }, { key: 'flowRate', value: fr }] } }));

  { const sv = (await ok(rq.get(`/api/sequfloh/tracking/${id}`))).stateVersion; await ok(rq.post(`/api/sequfloh/tracking/${id}/actions/start`, { data: { operationId: crypto.randomUUID(), stateVersion: sv } })); } // clocking on needs a started work order
  await page.goto(`/sequfloh/record/${id}`);
  await view(page).getByTestId('start-timer').click();
  await expect(view(page).getByTestId('stop-timer')).toBeVisible();
  await page.waitForTimeout(1500);
  await view(page).getByTestId('stop-timer').click();

  // quantity is edited on the Record tab only, and the header follows at once
  const qty = view(page).getByTestId('wo-quantity');
  await qty.getByTestId('qty-edit').click();
  await qty.getByTestId('qty-completed-input').fill('3');
  await qty.getByTestId('qty-save').click();
  await expect(qty.getByTestId('qty-completed')).toHaveText('3');
  await expect(view(page)).toContainText('3 of 3');
  await expect(view(page).getByTestId('summary-clocked')).toBeVisible();
  await qty.scrollIntoViewIfNeeded();
  await shot('01-record-tab-quantity');
  await view(page).getByRole('tab', { name: 'Job' }).click();
  await expect(view(page).getByTestId('wo-quantity')).toHaveCount(0);
  // no View task events: no tab, no option
  await expect(view(page).getByRole('tab', { name: 'Task events' })).toHaveCount(0);

  await view(page).getByTestId('wo-job-report').click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByLabel('Include detailed time breakdown')).not.toBeChecked();
  await expect(dlg.getByLabel('Include task events')).toHaveCount(0);
  await shot('02-job-report-options');
  await dlg.getByRole('button', { name: 'Cancel' }).click();

  const pdf = await rq.get(`/api/sequfloh/tracking/${id}/print`);
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  const body = await pdf.body();
  if (shots) fs.writeFileSync(path.join(shots, 'e2e-test-work-order-job-report.pdf'), body);
  const text = body.toString('latin1');
  expect((text.match(/\/Type \/Page\b/g) ?? []).length).toBe(1);
  for (const s of ['Test results', 'Part 3', 'Overall test result: FAIL', 'Total time', 'Personnel', '3 of 3']) expect(text).toContain(s);
  for (const s of ['Task events', 'No steps.', 'Validation form']) expect(text).not.toContain(s);
  expect((await rq.get(`/api/sequfloh/tracking/${id}/print?events=true`)).status()).toBe(403);
});
