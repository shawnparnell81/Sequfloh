import path from 'node:path';
import { expect, test, type Browser } from '@playwright/test';
import { settledShot, signIn, view } from './support';

const pad = (n: number) => String(n).padStart(2, '0');
const local = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
const WHY = 'Rebuilt the made-up strut mount; the tablet was down so nobody clocked on';

/** Made-up records and accounts only. Add past time for a registered person on a work order's Time tab. */
test('add past time: permission, counted once, chargeback, audit, overlap', async ({ page, browser }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const stamp = Date.now().toString().slice(-6);
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Inspection', siteId: site, inspectionSubType: 'AQL', inspectionDescription: `Made-up past time ${stamp}`, workOrder: `WO-PT-${stamp}`, minBenchmarkMinutes: 0, maxBenchmarkMinutes: 600 } }))).id as string;
  const v = (await ok(rq.get(`/api/sequfloh/tracking/${wo}`))).record.stateVersion;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/actions/start`, { data: { operationId: crypto.randomUUID(), stateVersion: v } }));
  const cb = (await ok(rq.post('/api/sequfloh/chargebacks', { data: { workOrderNumber: `WO-PT-${stamp}`, trackingRecordId: wo } }))).id as string;

  // gating: someone without the permission does not see the button
  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `No past time ${stamp}`, description: 'made-up test role', permissions: ['sequfloh.access'] } }))) as string;
  const email = `nopast.${stamp}@example.test`, password = `Pw-${stamp}-${Math.random().toString(36).slice(2)}`;
  const uid = (await ok(rq.post('/api/admin/users', { data: { email, displayName: `Test nopast ${stamp}`, password, mustChangePassword: false, defaultSiteId: site } }))) as string;
  await ok(rq.post(`/api/admin/users/${uid}/roles`, { data: { roleId: role, siteId: site, departmentId: null } }));
  const as = async (b: Browser) => {
    const ctx = await b.newContext(); const p = await ctx.newPage();
    await p.goto('/'); await p.fill('input[type=email]', email); await p.fill('input[type=password]', password); await p.click('button[type=submit]');
    await expect(p.getByTestId('tab-strip')).toBeVisible();
    return { ctx, p };
  };
  const other = await as(browser);
  await other.p.goto(`/sequfloh/record/${wo}`);
  await view(other.p).getByRole('tab', { name: 'Time', exact: true }).click();
  await expect(view(other.p).getByTestId('time-total')).toBeVisible();
  await expect(view(other.p).getByTestId('add-past-time')).toHaveCount(0);
  const denied = await other.p.request.post(`/api/sequfloh/tracking/${wo}/time/past`, { data: { start: new Date(Date.now() - 5 * 3600_000).toISOString(), stop: new Date(Date.now() - 4 * 3600_000).toISOString(), reason: WHY } });
  expect(denied.status()).toBe(403);
  await other.ctx.close();

  // the admin clocks on and off normally first (so the same person must be counted once)
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/time/start`)); await ok(rq.post(`/api/sequfloh/tracking/${wo}/time/stop`));

  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByRole('tab', { name: 'Time', exact: true }).click();
  const btn = view(page).getByTestId('add-past-time');
  await expect(btn).toBeEnabled();
  await shot('add-past-time-button');
  await btn.click();
  const dlg = page.getByTestId('past-time-dialog');
  await expect(dlg).toBeVisible();
  await expect(page.getByTestId('past-person')).not.toHaveValue('');
  const start = new Date(Date.now() - 2 * 86400_000); start.setHours(7, 0, 0, 0);
  const stop = new Date(start.getTime() + 6.5 * 3600_000);
  await page.getByTestId('past-start').fill(local(start));
  await page.getByTestId('past-stop').fill(local(stop));
  await page.getByTestId('past-add-pause').click();
  await page.getByTestId('past-pause-0').fill(local(new Date(start.getTime() + 5 * 3600_000)));
  await page.getByTestId('past-continue-0').fill(local(new Date(start.getTime() + 5.5 * 3600_000)));
  // a short or placeholder reason is refused
  await page.getByTestId('past-reason').fill('forgot');
  await page.getByTestId('past-save').click();
  await expect(page.getByTestId('past-error')).toContainText('at least 20 characters');
  await page.getByTestId('past-reason').fill(WHY);
  await shot('add-past-time-dialog');
  await page.getByTestId('past-save').click();
  await expect(dlg).toHaveCount(0);

  // counted as registered time, the same person once
  const crew = await ok(rq.get(`/api/sequfloh/tracking/${wo}/crew`));
  expect(crew.userSeconds).toBeGreaterThanOrEqual(6 * 3600);
  expect(crew.userSeconds).toBeLessThan(6 * 3600 + 120);
  expect(crew.personnel).toBe(1);
  await expect(view(page).getByTestId('crew-personnel')).toContainText('1');
  await expect(view(page).getByTestId('crew-user-hours')).toContainText('6.0');
  const pre = await ok(rq.get(`/api/sequfloh/chargebacks/prefill/${wo}`));
  expect(pre.totalPersonnel).toBe(1);
  expect(Number(pre.userHours)).toBeGreaterThanOrEqual(6);
  expect((await ok(rq.get(`/api/sequfloh/chargebacks/${cb}`))).timeChangedNote).toContain('past time added');
  await view(page).getByTestId('toggle-time-log').click();
  await expect(view(page).getByTestId('time-log')).toContainText('added after the fact by');

  // audit entry
  const audit = await ok(rq.get(`/api/audit-trail?entityId=${wo}&action=PastTimeAdded`));
  const row = audit.items[0];
  expect(row.description).toContain('added 6.00 h of past time for');
  expect(row.description).toContain(`reason: ${WHY}`);

  // overlap with the same person's time on this work order is rejected
  await btn.click();
  await page.getByTestId('past-start').fill(local(new Date(start.getTime() + 3600_000)));
  await page.getByTestId('past-stop').fill(local(new Date(start.getTime() + 2 * 3600_000)));
  await page.getByTestId('past-reason').fill(WHY);
  await page.getByTestId('past-save').click();
  await expect(page.getByTestId('past-error')).toContainText('already has time on');
});
