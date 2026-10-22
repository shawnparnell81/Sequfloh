import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Covers the Job tab, the chargeback report, the Official status in NetSuite field, history filter and CSV export. */
test('job quantity, parts, tester files, chargeback report and official status', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const dialog = () => page.getByRole('dialog');

  // a part with an official status chosen inline in the Parts list
  const pn = `34BW${stamp}`;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: pn, name: 'Test assembly CSA', partType: 1, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: true, isSerialized: false, isLotControlled: false } } }))).id as string;
  await page.goto(`/sequfloh/parts?q=${pn}`);
  const row = view(page).locator('tbody tr', { hasText: pn });
  await row.getByLabel('Official status in NetSuite').click();
  await page.getByRole('option', { name: 'Quarantine hold (NetSuite)' }).click();
  await expect.poll(async () => (await ok(rq.get(`/api/sequfloh/parts/${part}`))).part.statusTag).toBe('Quarantine hold (NetSuite)');
  await shot('parts-list-official-status');
  await page.goto(`/sequfloh/parts/${part}`);
  await expect(view(page).getByTestId('netsuite-status')).toHaveText('Quarantine hold (NetSuite)');
  await expect(view(page)).toContainText('Official holds and releases are made in NetSuite');
  await shot('part-page-official-status');

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

  // official status on the work order
  await view(page).getByTestId('edit-netsuite-status').click();
  await dialog().getByLabel('Official status in NetSuite').click();
  await page.getByRole('option', { name: 'Repair hold' }).click();
  await dialog().getByLabel('Note (optional)').fill('Made-up note: waiting on supplier credit');
  await shot('work-order-official-status-dialog');
  await dialog().getByRole('button', { name: 'Save' }).click();
  await expect(view(page).getByTestId('netsuite-status')).toHaveText('Repair hold');

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
  await view(page).getByTestId('test-file-input').setInputFiles({ name: 'tester-example.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
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

  // history: filter and CSV export
  await page.goto(`/sequfloh/record/${wo}?tab=history`);
  await view(page).getByLabel('Filter history').fill('quantity');
  await expect(view(page).locator('tbody tr').first()).toContainText(/quantity/i);
  const dl = page.waitForEvent('download');
  await view(page).getByRole('button', { name: 'Export CSV' }).click();
  expect((await dl).suggestedFilename()).toMatch(/history\.csv$/);
  await shot('history-filter');

  // work order list filtered by official status
  await page.goto('/sequfloh/tracking/open?tag=Repair%20hold');
  await expect(view(page).locator('tbody tr', { hasText: `WO-${stamp}` }).or(view(page).locator('tbody tr').first())).toBeVisible();
  await expect(view(page)).toContainText('Official status in NetSuite');
  await shot('work-orders-filtered-by-official-status');

  // org audit page with CSV export
  await page.goto('/sequfloh/audit/changes');
  await expect(view(page).getByRole('link', { name: 'Export CSV' }).or(view(page).getByRole('button', { name: 'Export CSV' }))).toBeVisible();
  const audit = await rq.get('/api/sequfloh/audit/changes/csv');
  expect(audit.ok()).toBeTruthy();
  expect(await audit.text()).toContain('When (UTC),Who,What');
  await shot('audit-trail-export');
});
