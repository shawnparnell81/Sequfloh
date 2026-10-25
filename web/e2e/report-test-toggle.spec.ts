import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. The "Include test results report?" Yes / No choice and the ERP reference staying off the report. */
test('include test results report yes / no', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Toggle instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Repair' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-T${stamp}` } }))).id as string;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/job/quantity`, { data: { completed: 20 } }));
  await ok(rq.put(`/api/sequfloh/tracking/${wo}/job/findings`, { data: { description: 'Made-up example: cracked housings.', erpReference: 'ERP-998877' } }));
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/job/test-files`, { multipart: { files: { name: 'tester-run.csv', mimeType: 'text/csv', buffer: Buffer.from('Serial,Result\nA1,PASS\nA2,FAIL\n') }, testDate: '2026-10-20' } }));

  // Findings tab still has the ERP reference field
  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByRole('tab', { name: 'Findings' }).click();
  await expect(view(page).getByLabel('ERP reference')).toHaveValue('ERP-998877');
  await shot('findings-tab-keeps-erp-reference');

  // report: default No, no test results anywhere, no ERP reference
  await page.goto(`/sequfloh/record/${wo}/report`);
  const report = view(page).getByTestId('job-report');
  const choice = view(page).getByTestId('include-tests');
  await expect(choice).toContainText('Include test results report?');
  await expect(choice.getByLabel('No')).toBeChecked();
  await view(page).getByLabel('Findings', { exact: true }).check();
  await expect(report).toContainText('Findings / nonconformance');
  await expect(report).not.toContainText(/test/i);
  await expect(report).not.toContainText('ERP');
  await shot('report-default-no');
  await view(page).getByTestId('full-detail').click();
  await expect(choice.getByLabel('No')).toBeChecked();
  await expect(report).toContainText('Findings / nonconformance');
  await expect(report).not.toContainText(/test/i);
  await shot('report-full-detail-keeps-no');
  const pdfNo = (await (await rq.get(`/api/sequfloh/tracking/${wo}/job/report/pdf?full=true`)).body()).toString('latin1');
  expect(pdfNo).not.toContain('Test results');
  expect(pdfNo).not.toContain('tester-run.csv');
  expect(pdfNo).not.toContain('998877');

  // Yes: test results come back and the answer is remembered for this work order
  await choice.getByLabel('Yes').check();
  await expect(report).toContainText('Test results');
  await expect(report).toContainText('tester-run.csv');
  await expect(report).not.toContainText('ERP');
  await shot('report-yes');
  await page.reload();
  await expect(view(page).getByTestId('include-tests').getByLabel('Yes')).toBeChecked();
  await view(page).getByTestId('include-tests').getByLabel('No').check();
  await page.reload();
  await expect(view(page).getByTestId('include-tests').getByLabel('No')).toBeChecked();
  await expect(view(page).getByTestId('job-report')).not.toContainText(/test/i);
  await shot('report-no-remembered');
});
