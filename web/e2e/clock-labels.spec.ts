import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Clock on / pause for break or lunch / clock back on / clock off, and the reasons in the time log and report. */
test('job time: clock on, pause for break and lunch, clock off', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Clock instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Work' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-C${stamp}` } }))).id as string;

  await page.goto(`/sequfloh/record/${wo}?tab=time`);
  const v = view(page);
  await expect(v.getByRole('button', { name: 'Clock on job' })).toBeVisible();
  await shot('clock-on-job');
  await v.getByRole('button', { name: 'Clock on job' }).click();
  await expect(v.getByRole('button', { name: 'Pause job – Break' })).toBeVisible();
  await expect(v.getByRole('button', { name: 'Pause job – Lunch' })).toBeVisible();
  await expect(v.getByRole('button', { name: 'Clock off job' })).toBeVisible();
  await shot('clocked-on-pause-and-clock-off');
  await v.getByRole('button', { name: 'Pause job – Break' }).click();
  await expect(v.getByRole('button', { name: 'Clock back on job' })).toBeVisible();
  await expect(v).toContainText('Paused for break since');
  await shot('paused-for-break');
  await v.getByRole('button', { name: 'Clock back on job' }).click();
  await v.getByRole('button', { name: 'Pause job – Lunch' }).click();
  await expect(v).toContainText('Paused for lunch since');
  await shot('paused-for-lunch');
  await v.getByRole('button', { name: 'Clock back on job' }).click();
  await v.getByRole('button', { name: 'Clock off job' }).click();
  await expect(v.getByRole('button', { name: 'Clock on job' })).toBeVisible();
  await v.getByTestId('toggle-time-log').click();
  const log = v.getByTestId('time-log');
  for (const t of ['Clocked on job', 'Paused for break', 'Paused for lunch', 'Clocked back on job', 'Clocked off job']) await expect(log).toContainText(t);
  await shot('time-log-with-reasons');

  await page.goto(`/sequfloh/record/${wo}/report`);
  await v.getByTestId('full-detail').click();
  const report = v.getByTestId('job-report');
  await expect(report).toContainText('Paused for lunch');
  await report.getByText('Detailed time log').scrollIntoViewIfNeeded();
  await shot('report-time-log');
  const pdf = await rq.get(`/api/sequfloh/tracking/${wo}/job/report/pdf?full=true`);
  expect(pdf.ok()).toBeTruthy();
});
