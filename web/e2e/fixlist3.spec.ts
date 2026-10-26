import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

const shots = process.env.FL3_SHOTS;
const shot = async (page: Page, name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };

test('completed validation/FAI: import on a family tab lands in that family; record actions with archive under More', async ({ page }) => {
  await signIn(page);
  const fam = (await (await page.request.get('/api/sequfloh/validation-forms')).json()).families[0] as { id: string; label: string };
  const stamp = Date.now().toString().slice(-6);
  await page.goto(`/sequfloh/completed-forms?family=${fam.id}`);
  const bar = view(page).locator('[data-record-actions="completed-forms"]');
  for (const a of ['print', 'import', 'fullscreen']) await expect(bar.locator(`[data-action="${a}"]`).first()).toBeVisible();
  await shot(page, 'completed-forms-family-tab');
  await view(page).getByTestId('completed-forms-files-input').setInputFiles({ name: `Completed check ${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('Check,Result\nTorque,OK\n') });
  const dlg = page.getByTestId('bulk-import');
  await expect(dlg.getByTestId('completed-import-family')).toContainText(fam.label);
  await shot(page, 'completed-forms-import-dialog');
  await dlg.getByTestId('bulk-import-start').click();
  await expect(dlg.getByText('Imported', { exact: true })).toBeVisible();
  await dlg.getByRole('button', { name: 'Close' }).click();
  const row = view(page).getByRole('row').filter({ hasText: `Completed check ${stamp}` });
  await expect(row).toBeVisible();

  // the completed form: shared record actions, Edit moves families, Archive and Delete under More
  await page.goto(`/sequfloh/imported-files/${await row.getByTestId('completed-open').getAttribute('data-id')}`);
  const fb = view(page).locator('[data-record-actions="completed-form"]');
  for (const a of ['edit', 'print', 'fullscreen']) await expect(fb.locator(`[data-action="${a}"]`).first()).toBeVisible();
  await fb.getByTestId('more-actions').click();
  await expect(page.getByRole('menu').getByTestId('archive-file')).toBeVisible();
  await expect(page.getByRole('menu').getByTestId('delete-button')).toBeVisible();
  await shot(page, 'completed-form-more-menu');
  await page.getByRole('menu').getByTestId('archive-file').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Archive' }).click();
  await expect(page).toHaveURL(new RegExp(`/sequfloh/completed-forms\\?family=${fam.id}`));
  await expect(view(page).getByRole('row').filter({ hasText: `Completed check ${stamp}` })).toHaveCount(0);

  // unassigned tab asks for a family (optional)
  await view(page).getByTestId('completed-unassigned').click();
  await expect(page).toHaveURL(/family=none/);
  await view(page).getByTestId('completed-forms-files-input').setInputFiles({ name: `Loose ${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('a,b\n1,2\n') });
  await expect(page.getByTestId('bulk-import').getByText('Leave empty to save the forms as Unassigned.')).toBeVisible();
  await page.getByTestId('bulk-import').getByRole('button', { name: 'Cancel' }).click();
});

test('work orders section: pages in order, no "Tracking" labels, New work order on each', async ({ page }) => {
  await signIn(page);
  await section(page, 'Work Orders');
  const labels = ['Open Work Orders', 'Validation / FAI', 'Repair / Rework', 'Inspection', 'Warranty', 'Salt Spray Test', 'Development', 'Production', 'Completed Work Orders'];
  await expect(view(page).getByTestId('section-page')).toHaveText(labels);
  await shot(page, 'work-orders-section');
  for (const l of labels) {
    await view(page).getByTestId('section-page').filter({ hasText: new RegExp(`^${l.replace('/', '\\/')}$`) }).click();
    await expect(view(page).getByTestId('new-work-order')).toBeVisible();
    await expect(view(page).locator('h1')).not.toContainText('Tracking');
  }
  await page.goto('/sequfloh/tracking/validation');
  await expect(view(page).getByTestId('new-work-order')).toBeVisible();
});

test('create report: period picker, filters and the period tick boxes build the summary with Print, PDF and Excel', async ({ page }) => {
  await signIn(page);
  await page.goto('/sequfloh/chargebacks/new');
  const form = view(page).getByTestId('chargeback-form');
  await form.getByTestId('cb-period-kind').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Week', exact: true }).click();
  const today = new Date().toLocaleDateString('en-CA');
  await form.getByTestId('cb-period-from').fill(today);
  await expect(form.getByTestId('cb-filters')).toBeVisible();
  await expect(form.getByTestId('cb-period-to')).toHaveCount(0);
  const keys = ['sumHours', 'sumByType', 'sumBySubType', 'sumByFamily', 'sumByPerson', 'sumCounts', 'sumQuality', 'sumChargebacks', 'sumPauses', 'sumDetail', 'sumCharts'];
  for (const k of keys) { await expect(form.getByTestId(`section-${k}`)).not.toBeChecked(); await form.getByTestId(`section-${k}`).check(); }
  for (const k of ['findings', 'notes', 'signoff']) await form.getByTestId(`section-${k}`).uncheck();
  await form.getByLabel('Report number').fill(`RPT-WK-${Date.now()}`); // the company's own report number
  await form.getByTestId('cb-period').scrollIntoViewIfNeeded();
  await shot(page, 'create-report-period-and-summary-sections');
  await view(page).getByTestId('save-chargeback').click();
  const v = view(page).getByTestId('chargeback-view');
  for (const h of ['Period', 'Total hours', 'Hours by work order type', 'Hours by product family', 'Hours by person', 'Work order counts', 'Inspection results', 'Chargeback totals', 'Pause and hold time by reason', 'Work order detail', 'Where the time went'])
    await expect(v).toContainText(h);
  await expect(v).not.toContainText(/\bpay\b|audit|compliance/i);
  const id = page.url().split('/chargebacks/')[1].split('/')[0];
  const bar = view(page).locator('[data-record-actions="chargeback-report"]');
  await expect(bar.locator('[data-action="print"]').first()).toBeVisible();
  await expect(view(page).getByTestId('cb-xlsx')).toBeVisible();
  await v.locator('[data-section="period"]').scrollIntoViewIfNeeded();
  await shot(page, 'create-report-period-summary-view');
  await v.locator('[data-section="sumCharts"]').scrollIntoViewIfNeeded();
  await shot(page, 'create-report-period-charts');
  const pdf = await page.request.get(`/api/sequfloh/chargebacks/${id}/pdf`);
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  const xlsx = await page.request.get(`/api/sequfloh/chargebacks/${id}/xlsx`);
  expect(xlsx.ok()).toBeTruthy();

  // custom range with a type filter shows the To date and keeps the filter on reopen
  await page.goto(`/sequfloh/chargebacks/${id}`);
  await form.getByTestId('cb-period-kind').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Custom range' }).click();
  await form.getByTestId('cb-period-to').fill(today);
  await form.getByTestId('cb-filter-type').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Inspection', exact: true }).click();
  await shot(page, 'create-report-custom-range-filters');
  await view(page).getByTestId('save-chargeback').click();
  await expect(v).toContainText('type Inspection');
});

/** A small valid one-page PDF with a line of made-up text. */
function madeUpPdf(text: string) {
  const stream = `BT /F1 18 Tf 72 720 Td (${text}) Tj ET`;
  const objs = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
    `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
  let out = '%PDF-1.4\n'; const offs: number[] = [];
  objs.forEach((o, i) => { offs.push(out.length); out += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const x = out.length;
  out += `xref\n0 ${objs.length + 1}\n0000000000 65535 f \n${offs.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('')}trailer\n<< /Size ${objs.length + 1} /Root 1 0 R >>\nstartxref\n${x}\n%%EOF\n`;
  return Buffer.from(out, 'latin1');
}

test('completed validation/FAI: a PDF imported on a family tab is kept as uploaded and opens in the PDF viewer', async ({ page }) => {
  await signIn(page);
  const fam = (await (await page.request.get('/api/sequfloh/validation-forms')).json()).families[0] as { id: string; label: string };
  const stamp = Date.now().toString().slice(-6);
  await page.goto(`/sequfloh/completed-forms?family=${fam.id}`);
  await view(page).getByTestId('completed-forms-files-input').setInputFiles({ name: `Final FAI ${stamp}.pdf`, mimeType: 'application/pdf', buffer: madeUpPdf(`Made-up final FAI ${stamp}`) });
  const dlg = page.getByTestId('bulk-import');
  await expect(dlg).toContainText('.pdf');
  await dlg.getByTestId('bulk-import-start').click();
  await expect(dlg.getByText('Imported', { exact: true })).toBeVisible();
  await dlg.getByRole('button', { name: 'Close' }).click();
  const row = view(page).getByRole('row').filter({ hasText: `Final FAI ${stamp}` });
  await expect(row).toBeVisible();
  await page.goto(`/sequfloh/imported-files/${await row.getByTestId('completed-open').getAttribute('data-id')}`);
  await expect(view(page).getByTestId('completed-pdf-frame')).toBeVisible();
  const fb = view(page).locator('[data-record-actions="completed-form"]');
  for (const a of ['print', 'fullscreen']) await expect(fb.locator(`[data-action="${a}"]`).first()).toBeVisible();
  await expect(fb.locator('[data-action="edit"]')).toBeVisible(); // number and title stay editable on a PDF
  await expect(view(page).getByTestId('upload-version')).toHaveCount(0);
  const id = page.url().split('/imported-files/')[1].split(/[?/]/)[0];
  const r = await page.request.get(`/api/imported-files/${id}/download?view=true`);
  expect(r.headers()['content-type']).toContain('application/pdf');
  expect(r.headers()['content-disposition']).toContain('inline');
  await page.waitForTimeout(800);
  await shot(page, 'completed-forms-pdf-import');
  await fb.getByTestId('more-actions').click();
  await expect(page.getByTestId('archive-file')).toBeVisible();
  await page.keyboard.press('Escape');
  expect((await page.request.post(`/api/imported-files/${id}/archive`, { data: {} })).ok()).toBeTruthy();
});

test('create report: the person filter lists everyone with recorded time, including day workers with no account', async ({ page }) => {
  await signIn(page);
  const rq = page.request;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Person filter instruction ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Check' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-P${stamp}` } }))).id as string;
  const worker = `Made-up Day Worker ${stamp}`;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/crew/workers`, { data: { name: worker } }));
  const wid = ((await ok(rq.get(`/api/sequfloh/tracking/${wo}/crew`))).workers as { id: string; name: string }[]).find((w) => w.name === worker)!.id;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/crew/start`, { data: { crewSize: 1, workerId: wid } }));
  await page.waitForTimeout(1500);
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/crew/stop?workerId=${wid}`, { data: {} }));

  await page.goto('/sequfloh/chargebacks/new');
  const form = view(page).getByTestId('chargeback-form');
  await form.getByTestId('cb-period-kind').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Single day' }).click();
  await form.getByTestId('cb-period-from').fill(new Date().toLocaleDateString('en-CA'));
  const person = form.getByTestId('cb-filter-person').locator('xpath=..').getByRole('combobox');
  await expect(person).toHaveText(/All people/);
  await person.click();
  const opts = (await page.getByRole('listbox').getByRole('option').allTextContents()).slice(1);
  expect(opts).toContain(worker);
  expect(opts).toEqual([...opts].sort((a, b) => { const x = a.toUpperCase(), y = b.toUpperCase(); return x < y ? -1 : x > y ? 1 : 0; }));
  await shot(page, 'create-report-person-dropdown');
  await page.getByRole('option', { name: worker, exact: true }).click();
  // the list follows the filters: a type with no time for the worker drops them and resets to All people
  await form.getByTestId('cb-filter-type').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Repair / Rework', exact: true }).click();
  await expect(person).toHaveText(/All people/);
  await form.getByTestId('cb-filter-type').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'All types' }).click();
  // an optional site narrows to that site's work orders
  const siteBox = form.getByTestId('cb-filter-site').locator('xpath=..').getByRole('combobox');
  await expect(siteBox).toHaveText(/All sites/);
  await siteBox.click();
  await page.getByRole('listbox').getByRole('option').nth(1).click();
  await person.click();
  await page.getByRole('option', { name: worker, exact: true }).click();
  // a linked work order lists only the people with time on that job
  expect(await ok(rq.get(`/api/sequfloh/chargebacks/people?trackingRecordId=${wo}`))).toEqual([worker]);
  for (const k of ['sumHours', 'sumByPerson']) await form.getByTestId(`section-${k}`).check();
  await form.getByLabel('Report number').fill(`RPT-P-${Date.now()}`);
  await view(page).getByTestId('save-chargeback').click();
  const v = view(page).getByTestId('chargeback-view');
  await expect(v).toContainText(`person ${worker}`);
  const rows = v.locator('[data-section="sumByPerson"] tbody tr');
  await expect(rows.first()).toContainText(worker);
  await expect(rows).toHaveCount(2); // the worker and the total
});
