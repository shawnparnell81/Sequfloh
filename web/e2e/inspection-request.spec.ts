import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up parts and requests only. Quick-action placements, the request form, the priority-ordered list, conversion and delete. */
test('inspection requests: quick action, list order, convert to work order, delete', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const pn = `IRX${stamp}`;
  await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Example bracket assembly', partType: 1, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false } } }));
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Inspection instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Inspect' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));

  // the three required placements
  const button = () => view(page).getByRole('button', { name: 'New inspection request' });
  await page.goto('/sequfloh');
  await expect(button()).toBeVisible();
  await shot('button-on-home');
  await page.goto('/sequfloh/instructions/all');
  await expect(button()).toBeVisible();
  await shot('button-on-work-instructions');
  await page.goto('/sequfloh/tracking/open');
  await expect(button()).toBeVisible();
  await shot('button-on-work-orders');

  // form in a dialog, part picked from the Part Master fills the description
  await button().click();
  const dlg = page.getByRole('dialog');
  await dlg.getByLabel('Part number').fill(pn);
  await page.getByRole('option', { name: new RegExp(pn) }).click();
  await expect(dlg.getByLabel('Part description')).toHaveValue('Example bracket assembly');
  await dlg.getByLabel('Qty (per NetSuite)').fill('40');
  await dlg.getByLabel('Priority').click(); await page.getByRole('option', { name: 'High' }).click();
  await dlg.getByLabel('Reason').click(); await page.getByRole('option', { name: 'Warranty claim' }).click();
  await dlg.getByLabel('Requested completion date').fill('2026-11-05');
  await dlg.getByLabel('Inspection requirement').fill('Check all mounting hole positions and the finish per the linked instruction.');
  await shot('new-request-dialog');
  await dlg.getByRole('button', { name: 'Submit request' }).click();
  await expect(page).toHaveURL(/inspection-request\//);
  const id = page.url().split('/').pop()!;

  // a few more for the order: overdue critical, free-entry low
  await ok(rq.post('/api/sequfloh/inspection-requests', { data: { siteId: site, partNumber: `FREE-${stamp}`, partDescription: 'Supplier sample lot', quantity: 5, reason: 'AQL sample result', priority: 'Low', requestedDate: '2026-10-30' } }));
  await ok(rq.post('/api/sequfloh/inspection-requests', { data: { siteId: site, partNumber: pn, quantity: 2, reason: 'Customer request', priority: 'Critical', requestedDate: '2026-10-15' } }));
  const del = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { siteId: site, partNumber: `DEL-${stamp}`, priority: 'Normal', requestedDate: '2026-11-20' } }))).id as string;
  await page.goto(`/sequfloh/inspection-requests`);
  await view(page).getByLabel('Search').fill(stamp);
  const rows = view(page).locator('tbody tr');
  await expect(rows).toHaveCount(4);
  await expect(rows.nth(0)).toContainText('Critical');
  await expect(rows.nth(0)).toContainText('Overdue');
  await expect(rows.nth(3)).toContainText('Low');
  await shot('list-priority-then-date');

  // convert
  await page.goto(`/sequfloh/inspection-request/${id}`);
  await shot('request-detail');
  await view(page).getByRole('button', { name: 'Convert to work order' }).click();
  const cd = page.getByRole('dialog');
  await cd.getByLabel('Work instruction').click();
  await page.getByRole('option', { name: new RegExp(`Inspection instruction ${stamp}`) }).click();
  await shot('convert-dialog');
  await cd.getByRole('button', { name: 'Create work order' }).click();
  await expect(page).toHaveURL(/sequfloh\/record\//);
  await expect(page.getByRole('dialog')).toBeHidden();
  await expect(view(page)).toContainText(pn);
  await shot('converted-work-order');
  const r = await ok(rq.get(`/api/sequfloh/inspection-requests/${id}`));
  expect(r.status).toBe('InProgress');
  expect(r.workOrderNumber).toBeTruthy();

  // dispatch queue shows open requests
  await page.goto('/sequfloh/execution/dispatch');
  await expect(view(page)).toContainText('Open inspection requests');
  await shot('dispatch-queue-open-requests');

  // delete
  await page.goto(`/sequfloh/inspection-request/${del}`);
  await view(page).getByTestId('delete-button').click();
  await shot('delete-confirm');
  await page.getByRole('dialog').getByRole('button', { name: /Delete/ }).click();
  await expect(page).toHaveURL(/inspection-requests$/);
  expect((await rq.get(`/api/sequfloh/inspection-requests/${del}`)).status()).toBe(404);
});
