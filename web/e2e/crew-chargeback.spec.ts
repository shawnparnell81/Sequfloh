import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. Crew time next to registered clock on/off, bulk backdated crew time, and a chargeback report with PDF and Excel. */
test('crew time, bulk backdate and chargeback report', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const dialog = () => page.getByRole('dialog');

  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { type: 'Validation', title: `Crew rebox ${stamp}`, siteId: site } }))).id as string;
  const doc = (await ok(rq.get(`/api/sequfloh/instructions/${wi}`))).instruction;
  await ok(rq.put(`/api/sequfloh/instructions/${wi}`, { data: { title: doc.title, siteId: site, departmentId: null, ownerId: null, partId: null, effectiveDate: null,
    content: { validationAcceptance: { overallAcceptanceCriteria: 'Made-up check passes' } }, steps: [{ sequence: 10, title: 'Inspect and rebox' }], characteristics: [], partsRows: [], rowVersion: doc.rowVersion ?? 0 } }));
  await ok(rq.post(`/api/sequfloh/instructions/${wi}/actions/release`, { data: { effectiveDate: '2026-10-01' } }));
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Production', workInstructionId: wi, siteId: site, workOrder: `WO-CB-${stamp}` } }))).id as string;

  // a registered person clocks on and off the usual way
  await page.goto(`/sequfloh/record/${wo}`);
  await view(page).getByTestId('start-timer').click();
  await expect(view(page).getByTestId('stop-timer')).toBeVisible();

  // crew of 3 runs at the same time: start, pause, continue, stop
  const crew = view(page).getByRole('region', { name: 'Crew time' }).or(view(page).locator('section[aria-label="Crew time"]')).first();
  await crew.getByTestId('crew-size').fill('3');
  await crew.getByTestId('crew-start').click();
  await expect(crew.getByTestId('crew-pause')).toBeVisible();
  await page.waitForTimeout(1200);
  await crew.getByTestId('crew-pause').click();
  await crew.getByTestId('crew-continue').click();
  await page.waitForTimeout(800);
  await crew.getByTestId('crew-stop').click();
  await expect(crew.getByTestId('crew-start')).toBeVisible();
  await view(page).getByTestId('stop-timer').click();
  await expect(view(page).getByTestId('start-timer')).toBeVisible();
  await expect(crew.getByTestId('crew-sessions').locator('tbody tr')).toHaveCount(1);

  // bulk: 3 workers x 10 working days x 8 h = 240 labor hours
  await crew.getByTestId('crew-bulk').click();
  await dialog().getByLabel('From').fill('2026-09-07');
  await dialog().getByLabel('To', { exact: true }).fill('2026-09-18');
  await dialog().getByLabel('Crew size (workers)').fill('3');
  await dialog().getByLabel('Hours per day').fill('8');
  await expect(dialog().getByTestId('bulk-summary')).toContainText('3 workers × 10 working days × 8 h/day = 240 labor hours');
  await shot('02-bulk-backdate-dialog');
  await dialog().getByTestId('bulk-save').click();
  await expect(dialog()).toHaveCount(0);
  await expect(crew.getByTestId('crew-sessions').locator('tbody tr')).toHaveCount(11);
  await expect(crew.getByTestId('crew-labor')).toHaveText(/^240\.0\d$/);
  const sum = await ok(rq.get(`/api/sequfloh/tracking/${wo}/crew`));
  expect(sum.users).toBe(1);
  expect(sum.personnel).toBe(4);
  expect(sum.userSeconds).toBeGreaterThan(0);
  expect(sum.totalSeconds).toBe(sum.crewLaborSeconds + sum.userSeconds);
  await expect(crew.getByTestId('crew-personnel')).toHaveText('4');
  await crew.scrollIntoViewIfNeeded();
  if (shots) await crew.screenshot({ path: path.join(shots, '01-work-order-crew-panel.png') });

  // chargeback report from the work order, hours filled from crew + registered time
  await view(page).getByTestId('wo-create-chargeback').click();
  const form = view(page).getByTestId('chargeback-form');
  await expect(form.getByTestId('cb-hours-from-wo')).toContainText('crew 240.0');
  await expect(form.getByLabel('Total personnel on the task')).toHaveValue('4');
  await form.getByLabel('Supplier', { exact: true }).fill('Made-up Supplier Co');
  await form.getByRole('combobox', { name: 'Part number' }).fill(`34BW-CB-${stamp}`);
  await form.getByLabel('Description of the inspection / rebox task').fill('Inspect and rebox made-up injectors');
  await form.getByLabel('Result').click();
  await page.getByRole('option', { name: 'Fail' }).click();
  await form.getByLabel('Qty inspected / reworked').fill('500');
  await form.getByLabel('Qty bad').fill('35');
  await form.getByLabel('Hourly rate (optional)').fill('25');
  await form.getByLabel('Notes').fill('Made-up notes for the supplier');
  if (shots) await form.screenshot({ path: path.join(shots, '03-chargeback-report-form.png'), animations: 'disabled', caret: 'hide' });
  await view(page).getByTestId('save-chargeback').click();
  const v = view(page).getByTestId('chargeback-view');
  await expect(v).toContainText('Made-up Supplier Co');
  await expect(v).toContainText('Crew labor hours');
  const id = page.url().split('/chargebacks/')[1].split('/')[0];

  const pdf = await rq.get(`/api/sequfloh/chargebacks/${id}/pdf`);
  expect(pdf.ok()).toBeTruthy();
  expect(pdf.headers()['content-type']).toContain('application/pdf');
  const xlsx = await rq.get(`/api/sequfloh/chargebacks/${id}/xlsx`);
  expect(xlsx.ok()).toBeTruthy();
  expect(xlsx.headers()['content-type']).toContain('spreadsheetml');
  if (shots) { fs.writeFileSync(path.join(shots, 'chargeback.pdf'), await pdf.body()); fs.writeFileSync(path.join(shots, 'chargeback.xlsx'), await xlsx.body()); }

  await page.goto('/sequfloh/chargebacks');
  await expect(view(page).getByTestId('chargeback-list')).toContainText(`WO-CB-${stamp}`);
});
