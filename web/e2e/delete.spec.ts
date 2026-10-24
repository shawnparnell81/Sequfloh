import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Covers Delete with its confirm, blocked deletes, several people on one work order and per-person timers. */
test('delete everywhere, several assignees and live time per person', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const dialog = () => page.getByRole('dialog');

  // a draft work instruction made by mistake
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Mistake instruction ${stamp}`, siteId: site } }))).id as string;
  await page.goto(`/sequfloh/instruction/${wi}`);
  await expect(view(page).getByTestId('delete-button')).toBeVisible();
  await shot('instruction-with-delete');
  await view(page).getByTestId('delete-button').click();
  await expect(dialog()).toContainText("This can't be undone.");
  await shot('instruction-delete-confirm');
  await dialog().getByRole('button', { name: 'Delete' }).click();
  await expect(page).toHaveURL(/\/sequfloh\/instructions\/validation/);
  expect((await rq.get(`/api/sequfloh/instructions/${wi}`)).status()).toBe(404);

  // a part that is no longer available
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `OLD-${stamp}`, name: 'Discontinued spacer', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false } } }))).id as string;
  await page.goto(`/sequfloh/parts/${part}`);
  await view(page).getByTestId('delete-button').click();
  await expect(dialog()).toContainText('the drawings stay');
  await shot('part-delete-confirm');
  await dialog().getByRole('button', { name: 'Delete' }).click();
  await expect(page).toHaveURL(/\/sequfloh\/parts$/);

  // a drawing folder that still has a drawing: blocked, with the reason in the dialog
  const folder = (await ok(rq.post('/api/drawings/folders', { data: { name: `Brackets ${stamp}`, siteId: site } }))).id as string;
  await ok(rq.post(`/api/drawings/folders/${folder}/drawings`, { multipart: {
    file: { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n') }, title: 'Test bracket', drawingNumber: `DWG-${stamp}`, revision: 'A' } }));
  await page.goto(`/sequfloh/drawings?folder=${folder}`);
  await view(page).getByRole('button', { name: 'Delete folder' }).click();
  await dialog().getByRole('button', { name: 'Delete' }).click();
  await expect(dialog().getByRole('alert')).toContainText('still holds 1 drawing');
  await shot('folder-delete-blocked');
  await dialog().getByRole('button', { name: 'Cancel' }).click();

  // a work order with two people and a running timer
  const rel = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Timer test instruction ${stamp}`, siteId: site } }))).id as string;
  const relDoc = (await ok(rq.get(`/api/sequfloh/instructions/${rel}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${rel}`, { data: { title: relDoc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check the test piece' }], characteristics: [], partsRows: [], rowVersion: relDoc.rowVersion ?? 0 } }));
  const relRes = await rq.post(`/api/sequfloh/instructions/${rel}/actions/release`, { data: { effectiveDate: '2026-10-01' } });
  expect(relRes.ok(), await relRes.text()).toBeTruthy();
  await ok(rq.post('/api/admin/users', { data: { email: `helper.${stamp}@example.test`, displayName: `Test Helper ${stamp}`, password: `Pw-${stamp}-${Math.random().toString(36).slice(2)}`, mustChangePassword: false, defaultSiteId: site } }));
  const me = (await ok(rq.get('/api/auth/session'))).user;
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: rel, siteId: site, assignedUserId: me.id } }))).id as string;
  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByTestId('edit-assignees').click();
  await dialog().getByLabel('People').fill(`Test Helper ${stamp}`);
  await page.getByRole('option', { name: `Test Helper ${stamp}` }).click();
  await dialog().getByRole('heading', { name: 'Assigned people' }).click();
  await shot('assign-two-people');
  await dialog().getByRole('button', { name: 'Save' }).click();
  await expect(view(page).getByTestId('assignees')).toContainText(`Test Helper ${stamp}`);
  await view(page).getByTestId('start-timer').click();
  await expect(view(page).getByTestId('stop-timer')).toBeVisible();
  await page.waitForTimeout(2500);
  await expect(view(page).getByTestId('time-total')).not.toHaveText('00:00:00');
  await expect(view(page).getByTestId('time-people')).toContainText('Clocked on since');
  await shot('timer-running-per-person');
  await view(page).getByTestId('stop-timer').click();
  await expect(view(page).getByTestId('start-timer')).toBeVisible();
  await expect(view(page).getByTestId('delete-button')).toHaveCount(0); // time logged: kept for chargebacks
  await shot('timer-stopped-no-delete');

  // the list shows everyone on the job
  await page.goto('/sequfloh/tracking/production');
  await expect(view(page).getByText(`Test Helper ${stamp}`).first()).toBeVisible();
  await shot('work-orders-list-assignees');

  // a work order made by mistake (no time) can be deleted
  const oops = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: rel, siteId: site } }))).id as string;
  await page.goto(`/sequfloh/record/${oops}`);
  await view(page).getByTestId('delete-button').click();
  await expect(dialog()).toContainText('logged time are kept');
  await shot('work-order-delete-confirm');
  await dialog().getByRole('button', { name: 'Delete' }).click();
  await expect(page).toHaveURL(/\/sequfloh\/tracking\/production/);

  // sites and departments
  await ok(rq.post('/api/organization/sites', { data: { code: `T${stamp}`, name: `Test site ${stamp}` } }));
  await page.goto('/admin/sites');
  await expect(view(page).getByText(`Test site ${stamp}`)).toBeVisible();
  await shot('sites-with-delete');
});
