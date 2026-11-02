import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

const shots = process.env.NOTIFY_SHOTS;

/** Made-up records and accounts only. A newly assigned person sees the bell count, a short message, and the work in My Work without reloading. */
test('assignment notifications: bell count, toast, open marks read, mark all read', async ({ page, browser }) => {
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const stamp = Date.now().toString().slice(-6);
  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `Notify ${stamp}`, description: 'made-up test role', permissions: ['sequfloh.access', 'sequfloh.view_dashboard'] } }))) as string;
  const email = `ntf.${stamp}@example.test`, password = `Pw-${stamp}-${Math.random().toString(36).slice(2)}`;
  const uid = (await ok(rq.post('/api/admin/users', { data: { email, displayName: `Test Tech ${stamp}`, password, mustChangePassword: false, defaultSiteId: site } }))) as string;
  await ok(rq.post(`/api/admin/users/${uid}/roles`, { data: { roleId: role, siteId: site, departmentId: null } }));
  const assign = async (n: string) => {
    const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${n}-${stamp}`, type: 'Inspection', siteId: site, inspectionSubType: 'AQL', inspectionDescription: `Made-up check ${n}`,
      workOrder: `WO-${n}-${stamp}`, minBenchmarkMinutes: 0, maxBenchmarkMinutes: 600 } }))).id as string;
    const v = (await ok(rq.get(`/api/sequfloh/tracking/${id}`))).record.stateVersion;
    await ok(rq.post(`/api/sequfloh/tracking/${id}/actions/dispatch`, { data: { operationId: crypto.randomUUID(), stateVersion: v, assignedUserId: uid } }));
    return id;
  };

  const ctx = await browser.newContext({ viewport: { width: 1400, height: 900 } }); const p = await ctx.newPage();
  await p.goto('/'); await p.fill('input[type=email]', email); await p.fill('input[type=password]', password); await p.click('button[type=submit]');
  await expect(view(p).getByTestId('my-work-empty')).toBeVisible();
  const bell = p.getByTestId('notification-bell');
  await expect(bell).toHaveAttribute('data-unread-count', '0');

  const first = await assign('N1');
  // coming back to the window checks straight away
  await p.evaluate(() => { window.dispatchEvent(new Event('focus')); document.dispatchEvent(new Event('visibilitychange')); });
  const toast = p.getByTestId('notification-toast');
  await expect(toast).toContainText(`E2E-N1-${stamp} was assigned to you by`);
  await expect(toast).toContainText('Made-up check N1');
  await expect(bell).toHaveAttribute('data-unread-count', '1');
  await expect(view(p).getByTestId('my-work-item')).toHaveCount(1);
  await expect(view(p).getByTestId('my-work-item').first()).toContainText(`WO-N1-${stamp}`);
  if (shots) await p.screenshot({ path: `${shots}/03-toast.png` });
  if (shots) await p.locator('header').screenshot({ path: `${shots}/01-bell-with-count.png` });

  await bell.click();
  const items = p.getByTestId('notification-item');
  await expect(items).toHaveCount(1);
  await expect(toast).toBeHidden();
  await p.waitForTimeout(400);
  if (shots) await p.screenshot({ path: `${shots}/02-dropdown.png` });
  await items.first().click();
  await expect(p).toHaveURL(new RegExp(`/sequfloh/record/${first}`));
  await expect(bell).toHaveAttribute('data-unread-count', '0');

  await assign('N2'); await assign('N3');
  await p.evaluate(() => { window.dispatchEvent(new Event('focus')); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(bell).toHaveAttribute('data-unread-count', '2');
  await bell.click();
  await expect(items).toHaveCount(3);
  await p.getByTestId('notification-read-all').click();
  await expect(bell).toHaveAttribute('data-unread-count', '0');
  await ctx.close();
});
