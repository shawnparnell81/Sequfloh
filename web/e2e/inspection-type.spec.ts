import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up part and request only. Inspection is offered as a work order type and is the default when a request is converted. */
test('inspection work order type: offered in the dropdown and the default on conversion', async ({ page }) => {
  const shots = process.env.E2E_TYPE_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const pn = `INT${stamp}`;
  await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Example housing', partType: 1, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false } } }));
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Inspect remaining inventory ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Inspect' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const id = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { siteId: site, partNumber: pn, quantity: 30, reason: 'Customer request', priority: 'High', requestedDate: '2026-11-10',
    requirement: 'Inspect the remaining inventory and separate any bad parts for repair.' } }))).id as string;

  await page.goto(`/sequfloh/inspection-request/${id}`);
  await view(page).getByRole('button', { name: 'Convert to work order' }).click();
  const cd = page.getByRole('dialog');
  const typeField = cd.getByLabel('Work order type');
  await expect(typeField).toContainText('Inspection');
  await typeField.click();
  const opts = page.getByRole('listbox').getByRole('option');
  await expect(opts).toHaveText(['Inspection', 'Production', 'FAI', 'Validation', 'Repair']);
  await shot('01-work-order-type-dropdown');
  await page.getByRole('option', { name: 'Inspection' }).click();
  await cd.getByRole('switch', { name: /Attach a work instruction/ }).check();
  await cd.getByRole('combobox', { name: 'Work instruction' }).click();
  await cd.getByRole('combobox', { name: 'Work instruction' }).fill(String(stamp)); // narrow the list once many instructions exist
  await page.getByRole('option', { name: new RegExp(`Inspect remaining inventory ${stamp}`) }).click();
  await cd.getByRole('button', { name: 'Create work order' }).click();
  await expect(page).toHaveURL(/sequfloh\/record\//);
  await expect(page.getByRole('dialog')).toBeHidden();
  await expect(view(page)).toContainText('Inspection work order');
  const woId = page.url().split('/').pop()!;
  expect((await ok(rq.get(`/api/sequfloh/tracking/${woId}`))).record.type).toBe('Inspection');
  await shot('02-converted-work-order-inspection');

  await page.goto('/sequfloh/tracking/open');
  await view(page).getByLabel('Search').fill(pn);
  await expect(view(page).locator('tbody tr').first()).toContainText('Inspection');
  await shot('03-work-orders-list-type-inspection');
});
