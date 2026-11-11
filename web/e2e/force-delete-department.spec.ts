import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. A department a cancelled work order still names can be deleted anyway, and the work order stays. */
test('delete a department that a cancelled work order still names', async ({ page }) => {
  const shots = '/opt/cursor/artifacts';
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const stamp = Date.now().toString().slice(-6);
  const site = (await ok(rq.post('/api/organization/sites', { data: { code: `G${stamp}`, name: `Greer ${stamp}` } }))) as string;
  const dept = (await ok(rq.post(`/api/organization/sites/${site}/departments`, { data: { code: `Q${stamp}`, name: `Greer quality ${stamp}` } }))) as string;
  const number = `E2E-${Math.random().toString(36).slice(2, 10)}`;
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number, type: 'Production', siteId: site, departmentId: dept } }))).id as string;
  const ver = (await ok(rq.get(`/api/sequfloh/tracking/${wo}`))).record.stateVersion;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/actions/cancel`, { data: { operationId: crypto.randomUUID(), stateVersion: ver, reason: 'Made by mistake' } }));

  await page.goto('/admin/sites');
  const v = view(page);
  await expect(v.getByText(`Greer ${stamp}`)).toBeVisible();
  await v.getByTestId(`delete-department-Q${stamp}`).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('department-delete-uses')).toContainText('1 work order: 1 cancelled');
  await expect(dialog.getByTestId('department-delete-anyway')).toBeDisabled();
  await settledShot(page, path.join(shots, 'screenshot_department_delete_blocked.png'));

  await dialog.getByTestId('department-delete-confirm').fill(`Q${stamp}`);
  await expect(dialog.getByTestId('department-delete-anyway')).toBeEnabled();
  await settledShot(page, path.join(shots, 'screenshot_department_delete_confirm.png'));
  await dialog.getByTestId('department-delete-anyway').click();

  await expect(v.getByTestId('department-delete-result')).toContainText(`Department Q${stamp} was deleted`);
  await expect(v.getByText(`Greer quality ${stamp}`)).toHaveCount(0);
  await settledShot(page, path.join(shots, 'screenshot_department_delete_result.png'));

  const still = await ok(rq.get(`/api/sequfloh/tracking/${wo}`));
  expect(still.record.status).toBe('Canceled');
  expect(still.record.departmentId).toBeNull();
  expect(still.record.department).toBe(`Greer quality ${stamp}`);
});
