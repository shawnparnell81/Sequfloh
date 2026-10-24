import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Covers the Job tab, the chargeback report, the NetSuite inventory note, history filter and CSV export. */
test('job quantity, parts, tester files, chargeback report and the NetSuite note', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const dialog = () => page.getByRole('dialog');

  // a part: no official status anywhere, only the note that inventory status lives in NetSuite
  const pn = `34BW${stamp}`;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Test assembly CSA', partType: 1, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: true, isSerialized: false, isLotControlled: false } } }))).id as string;
  await page.goto(`/sequfloh/parts?q=${pn}`);
  await expect(view(page).getByTestId('inventory-note')).toHaveText('Inventory status is kept in NetSuite. Sequfloh does not connect to NetSuite.');
  await expect(view(page)).not.toContainText('Official status');
  await shot('parts-list-inventory-note');
  await page.goto(`/sequfloh/parts/${part}`);
  await expect(view(page).getByTestId('inventory-note')).toBeVisible();
  await expect(view(page).getByTestId('netsuite-status')).toHaveCount(0);
  await shot('part-page-inventory-note');

  // released instruction and a work order
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Job report instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: part, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Repair and test' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-${stamp}` } }))).id as string;

  // anyone can start a job and log time without being assigned
  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByTestId('start-timer').click();
  await expect(view(page).getByTestId('stop-timer')).toBeVisible();
  await page.waitForTimeout(1500);
  await view(page).getByTestId('stop-timer').click();

  // no official status on the work order either
  await expect(view(page).getByTestId('edit-netsuite-status')).toHaveCount(0);
  await expect(view(page).getByTestId('inventory-note')).toBeVisible();

  // Job tab: quantity, parts, tester file
  await view(page).getByRole('tab', { name: 'Job' }).click();
  await view(page).getByLabel('Add completed').fill('300');
  await view(page).getByLabel('Add could not repair (optional)').fill('2');
  await view(page).getByRole('button', { name: 'Add', exact: true }).click();
  await expect(view(page).getByTestId('qty-completed')).toHaveText('300');
  await view(page).getByRole('button', { name: 'Edit parts' }).click();
  await view(page).getByRole('button', { name: 'Add part' }).click();
  await view(page).getByLabel('Part number').last().fill('123abc');
  await view(page).getByLabel('Description').last().fill('Top mount');
  await view(page).getByRole('button', { name: 'Save parts' }).click();
  await expect(view(page)).toContainText('123abc');
  await view(page).getByLabel('Batch (optional)').fill('B1');
  const csv = 'Unit,Outcome,Leak rate\n1,PASS,0.2\n2,PASS,0.3\n3,FAIL,1.9\n4,ok,0.25\n';
  await view(page).getByTestId('work-order-tester-files-files-input').setInputFiles({ name: 'tester-example.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await page.getByTestId('bulk-import-start').click();
  await expect(page.getByTestId('bulk-done')).toContainText('1 tester file imported');
  await page.getByTestId('bulk-import').getByRole('button', { name: 'Close' }).click();
  await expect(view(page)).toContainText('tester-example.csv');
  await expect(view(page)).toContainText('Tested 4');
  await shot('job-tab');
  await view(page).getByRole('button', { name: 'Columns' }).click();
  await expect(dialog()).toContainText('Leak rate');
  await shot('tester-columns-dialog');
  await dialog().getByRole('button', { name: 'Cancel' }).click();

  // chargeback report: print page and PDF, no official status on it
  await page.goto(`/sequfloh/record/${wo}/report`);
  await view(page).getByLabel('Supplier').fill('Supplier name (example)');
  const report = view(page).getByTestId('job-report');
  await expect(report).toContainText('Chargeback job report');
  await expect(report).toContainText('123abc');
  await expect(report).toContainText('____');
  await expect(report).not.toContainText('Official status');
  await expect(report).not.toContainText('Repair hold');
  await shot('job-report-page');
  await view(page).getByLabel('Rate per hour (optional)').fill('85');
  await expect(report).not.toContainText('Labour cost____');
  await shot('job-report-page-with-rate');
  const pdf = await rq.get(`/api/sequfloh/tracking/${wo}/job/report/pdf?rate=85`);
  expect(pdf.ok()).toBeTruthy();
  expect(pdf.headers()['content-type']).toContain('application/pdf');

  // record pages no longer carry a history tab; the trail lives on Admin and Settings
  await page.goto(`/sequfloh/record/${wo}?tab=history`);
  await expect(view(page).getByLabel('Filter history')).toHaveCount(0);

  // the work order list has no official status filter
  await page.goto('/sequfloh/tracking/open');
  await expect(view(page)).not.toContainText('Official status in NetSuite');

  // org audit page with CSV export
  await page.goto('/sequfloh/audit/changes');
  await expect(view(page).getByRole('link', { name: 'Export CSV' }).or(view(page).getByRole('button', { name: 'Export CSV' }))).toBeVisible();
  const audit = await rq.get('/api/sequfloh/audit/changes/csv');
  expect(audit.ok()).toBeTruthy();
  expect(await audit.text()).toContain('When (UTC),Who,What');
  await shot('audit-trail-export');
});
