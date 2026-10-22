import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Per-person time with pause, findings, evidence, cost lines and the report section options. */
test('time detail, findings and report options', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Detail instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Repair and test' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-D${stamp}` } }))).id as string;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/job/quantity`, { data: { completed: 300 } }));
  await ok(rq.put(`/api/sequfloh/tracking/${wo}/job/parts`, { data: [{ partNumber: '123abc', description: 'Top mount', perAssembly: 1, unitCost: 4.25 }] }));

  // Time tab: start, pause, resume, stop
  await page.goto(`/sequfloh/record/${wo}?tab=time`);
  await view(page).getByTestId('start-timer').click();
  await view(page).getByTestId('pause-timer').click();
  await expect(view(page).getByTestId('resume-timer')).toBeVisible();
  await expect(view(page)).toContainText('Paused since');
  await page.waitForTimeout(1200);
  await view(page).getByTestId('resume-timer').click();
  await view(page).getByTestId('stop-timer').click();
  await view(page).getByTestId('toggle-time-log').click();
  await expect(view(page).getByTestId('time-log')).toContainText('Pause');
  await expect(view(page).getByTestId('time-log')).toContainText('Resume');
  await expect(view(page).getByTestId('time-log')).toContainText('Stop');
  await shot('time-tab-pause-and-log');

  // Findings tab
  await view(page).getByRole('tab', { name: 'Findings' }).click();
  await view(page).getByTestId('discovery-method').click();
  await page.getByRole('option', { name: 'AQL sampling inspection' }).click();
  await view(page).getByLabel('Supplier', { exact: true }).fill('Example Supplier Co.');
  await view(page).getByLabel('Lot / date codes').fill('L2609-A, L2609-B');
  await view(page).getByLabel('Qty nonconforming').fill('300');
  await view(page).getByLabel('Description of the nonconformance').fill('Made-up example: top mount cracked at the weld.');
  await view(page).getByLabel(/Repair method/).fill('Replace top mount and leak test');
  await view(page).getByTestId('save-findings').click();
  await expect(view(page)).toContainText('Saved');
  await view(page).getByTestId('evidence-input').setInputFiles(new URL('./example-evidence.jpg', import.meta.url).pathname);
  await expect(view(page)).toContainText('example-evidence.jpg');
  await view(page).getByRole('button', { name: 'Edit cost lines' }).click();
  await view(page).getByRole('button', { name: 'Add line' }).click();
  await view(page).getByLabel('Cost description').fill('Freight');
  await view(page).getByLabel('Amount').fill('85');
  await view(page).getByRole('button', { name: 'Save cost lines' }).click();
  await expect(view(page)).toContainText('Freight');
  await shot('findings-tab');

  // report: summary by default, Full detail remembered
  await page.goto(`/sequfloh/record/${wo}/report`);
  const report = view(page).getByTestId('job-report');
  await expect(report).toContainText('Chargeback summary');
  await expect(report).not.toContainText('Findings / nonconformance');
  await shot('report-summary-only');
  await view(page).getByTestId('full-detail').click();
  await view(page).getByLabel('Rate per hour (optional)').fill('55');
  await expect(report).toContainText('Findings / nonconformance');
  await expect(report).toContainText('Detailed time log');
  await expect(report).toContainText('Photos and evidence');
  await expect(report).not.toContainText('Test results'); // ticked but empty
  await expect(report).not.toContainText('Official status');
  await shot('report-full-detail');
  await page.reload();
  await expect(view(page).getByLabel('Detailed time log')).toBeChecked();
  const pdf = await rq.get(`/api/sequfloh/tracking/${wo}/job/report/pdf?rate=55`);
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  const text = (await pdf.body()).toString('latin1');
  expect(text).toContain('Findings / nonconformance');
  expect(text).not.toContain('Official status');
  await view(page).getByTestId('summary-only').click();
  await expect(report).not.toContainText('Detailed time log');
});
