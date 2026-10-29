import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Test work orders: fuel pump spec once with Part 1, Part 2 results; a CSA test order shows no fuel pump fields. */
test('test work instruction: fuel pump parts against the spec, nothing for CSA', async ({ page }) => {
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
  const pumpPart = await mkPart(`FP-${k}`, 'Example in-tank fuel pump', pump.id);
  await mkPart(`CSA-${k}`, 'Example strut assembly', csa.id);
  const fs = await import('node:fs');
  await ok(rq.post(`/api/drawings/for-part/${pumpPart}/upload`, { multipart: { file: { name: `FP-${k}.jpg`, mimeType: 'image/jpeg', buffer: fs.readFileSync(new URL('./example-evidence.jpg', import.meta.url)) },
    title: `Fuel pump FP-${k}`, drawingNumber: `DWG-FP-${k}`, revision: 'A' } }));

  const startNew = async (pn: string, family: string) => {
    await page.goto('/sequfloh/tracking/new?type=Test');
    await page.getByTestId('create-clock-no').click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.getByLabel('Number *').fill(`E2E-TST-${pn}`);
    const box = view(page).getByLabel('Part (required)');
    await box.click(); await box.pressSequentially(pn, { delay: 20 });
    await page.getByTestId('part-option').filter({ hasText: pn }).first().click();
    await view(page).getByLabel('Product family').click();
    await page.getByRole('option', { name: family }).click();
  };
  const created = async (p: Page) => { await p.getByTestId('new-wo-create').click(); await p.waitForURL(/\/sequfloh\/record\/[0-9a-f-]{36}/); return p.url().split('/').pop()!.split('?')[0]; };

  // fuel pump: the drawing linked to the exact part is attached automatically
  await startNew(`FP-${k}`, pump.label);
  await expect(view(page).getByTestId('test-drawings')).toContainText(`DWG-FP-${k}`);
  await expect(page.getByLabel('Test work instruction (optional)')).toBeVisible();
  await shot('01-create-fuel-pump-test');
  const id = await created(page);
  const panel = view(page).getByTestId('test-results');
  await expect(panel).toBeVisible();
  for (const [key, v] of [['testPressure', '60'], ['flowRate', '100'], ['floatFull', '250'], ['floatEmpty', '40'], ['amps', '6.5']]) await panel.getByTestId(`spec-${key}`).fill(v);
  await expect(panel.getByTestId('spec-unit-flowRate')).toHaveValue('L/h');
  await panel.getByTestId('spec-save').click();
  await expect(panel.getByTestId('spec-save')).toHaveCount(0);
  await panel.getByTestId('test-add-part').click();
  const p1 = panel.getByTestId('test-part-1');
  await expect(p1).toContainText('Part 1 test results');
  for (const [key, v] of [['testPressure', '59'], ['flowRate', '95'], ['floatFull', '248'], ['floatEmpty', '41'], ['amps', '6.4']]) await p1.getByTestId(`part-1-${key}`).fill(v);
  await p1.getByTestId('part-1-note').fill('Made-up: first pump off the rack');
  await p1.getByTestId('part-1-save').click();
  await expect(p1.getByTestId('part-1-save')).toHaveCount(0);
  await panel.getByTestId('test-add-part').click();
  const p2 = panel.getByTestId('test-part-2');
  for (const [key, v] of [['testPressure', '61'], ['flowRate', '102'], ['floatFull', '251']]) await p2.getByTestId(`part-2-${key}`).fill(v);
  await p2.getByTestId('part-2-save').click();
  await expect(p2.getByTestId('part-2-save')).toHaveCount(0);
  await expect(p2.getByText('Blank')).toHaveCount(2);
  await expect(panel).not.toContainText('Serial');
  await panel.scrollIntoViewIfNeeded();
  await shot('02-order-page-two-part-results');
  const t = await ok(rq.get(`/api/sequfloh/tracking/${id}/test`));
  expect(t.parts.map((p: { partIndex: number }) => p.partIndex)).toEqual([1, 2]);
  expect(t.parts[0].values.find((v: { key: string }) => v.key === 'flowRate').value).toBe('95');
  const print = await rq.get(`/api/sequfloh/tracking/${id}/print`);
  expect(print.ok()).toBeTruthy();
  if (shots) fs.writeFileSync(path.join(shots, '03-printout.pdf'), await print.body());

  // CSA: no fuel pump fields anywhere; a part without a drawing offers a pick by hand
  await startNew(`CSA-${k}`, csa.label);
  await expect(view(page).getByTestId('test-no-drawing')).toHaveText('No drawing linked to this part');
  await shot('04-create-csa-test-no-drawing');
  await created(page);
  await expect(view(page).getByTestId('test-none')).toHaveText('No test fields set up for this product family yet.');
  await expect(view(page).getByTestId('test-results')).toHaveCount(0);
  await expect(view(page).getByText('Flow rate')).toHaveCount(0);
  await expect(view(page).getByText('Float arm resistance')).toHaveCount(0);
  await shot('05-csa-test-order-no-fields');
});
