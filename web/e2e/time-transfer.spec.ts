import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Time put on the wrong work order is moved to a new work order created from it. */
test('transfer time to a new work order', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const stamp = Date.now().toString().slice(-6);

  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Inspection', siteId: site, inspectionSubType: 'AQL', inspectionDescription: `Made-up wrong order ${stamp}`, workOrder: `WO-INS-${stamp}`, minBenchmarkMinutes: 0, maxBenchmarkMinutes: 600 } }))).id as string;
  const start = new Date(Date.now() - 26 * 3600_000); start.setMinutes(0, 0, 0);
  const stop = new Date(start.getTime() + 2 * 3600_000);
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/crew/sessions`, { data: { crewSize: 3, start: start.toISOString(), stop: stop.toISOString(), note: 'made-up crew' } }));

  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByRole('tab', { name: 'Time', exact: true }).click();
  const fix = view(page).getByTestId('time-corrections');
  await expect(fix.getByTestId('time-fix-row')).toHaveCount(1);
  await fix.getByTestId('time-fix-all').check();
  await shot('time-corrections');
  await fix.getByTestId('time-fix-new').click();
  await expect(page.getByTestId('new-wo-move')).toBeVisible();
  await shot('time-transfer-new-form');
  await page.getByLabel('Number *').fill(`E2E-${Math.random().toString(36).slice(2, 10)}`);
  await page.getByTestId('new-wo-create').click();
  await page.waitForURL((u) => !u.pathname.endsWith(wo) && /\/sequfloh\/record\//.test(u.pathname));
  const target = page.url().split('/').pop()!.split('?')[0];
  const moved = await ok(rq.get(`/api/sequfloh/tracking/${target}/crew`));
  expect(moved.crewLaborSeconds).toBe(6 * 3600);
  expect((await ok(rq.get(`/api/sequfloh/tracking/${wo}/crew`))).crewLaborSeconds).toBe(0);
  const rec = (await ok(rq.get(`/api/sequfloh/tracking/${target}`))).record;
  expect(rec.type).toBe('Inspection');
  expect(rec.workOrder).toBe(`WO-INS-${stamp}`);
});
