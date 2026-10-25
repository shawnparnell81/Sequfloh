import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Fix list 5: Edit changes a file's number and name (blank forms and PDFs too), and Salt Spray Test has its own confirmed form. Made-up records only. */
const shots = process.env.FL5_SHOTS;
const shot = async (page: Page, name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
const upload = async (page: Page, site: string, name: string, mimeType: string, body: string, flag: string) =>
  (await ok(page.request.post('/api/imported-files', { multipart: { siteId: site, [flag]: 'true', file: { name, mimeType, buffer: Buffer.from(body) } } }))).id as string;

test('Edit on a blank form and on a PDF changes both the number and the name', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  const site = (await ok(page.request.get('/api/organization/sites')) as { id: string }[])[0].id;
  const blank = await upload(page, site, `IMP blank ${stamp}.csv`, 'text/csv', 'Check,Result\nTorque,\n', 'blankForm');
  const pdf = await upload(page, site, `IMP pdf ${stamp}.pdf`, 'application/pdf', '%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n', 'completedForm');
  let i = 0;
  for (const [id, num, title] of [[blank, `FRM-VAL-${stamp}`, 'Fuel Pump Validation - Rev C'], [pdf, `PDF-${stamp}`, 'Final inspection scan']]) {
    await page.goto(`/sequfloh/imported-files/${id}`);
    const edit = view(page).locator('[data-record-actions] [data-action="edit"]');
    await expect(edit).toBeEnabled();
    await edit.click();
    const dlg = page.getByTestId('edit-number-dialog');
    await dlg.getByTestId('record-number').fill(num);
    await dlg.getByTestId('record-title').fill(title);
    await shot(page, `0${++i}-edit-${i === 1 ? 'blank-form' : 'pdf'}`);
    await dlg.getByTestId('edit-number-save').click();
    await expect(view(page).locator('h1')).toContainText(`${num} · ${title}`);
    const d = await ok(page.request.get(`/api/imported-files/${id}`));
    expect([d.number, d.title]).toEqual([num, title]);
  }
});

test('the Tests section confirms a Salt Spray Test form and a new Salt Spray Test work order gets a copy', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  const site = (await ok(page.request.get('/api/organization/sites')) as { id: string }[])[0].id;
  await ok(page.request.put('/api/sequfloh/validation-forms/tests/salt-spray', { data: { blankFormId: null } }));
  const blank = await upload(page, site, `Salt Spray Test Report ${stamp}.csv`, 'text/csv', 'Hours,Result\n96,\n', 'blankForm');
  const none = await ok(page.request.post('/api/sequfloh/tracking', { data: { type: 'SaltSpray', siteId: site, number: `SS-${stamp}-1` } }));
  expect(none.validationForm.fileId).toBeUndefined();

  await page.goto('/sequfloh/settings/validation-forms');
  const row = view(page).getByTestId('test-forms-table').getByTestId('vf-row-Salt Spray Test');
  await expect(row).toBeVisible();
  await view(page).getByTestId('vf-select-Salt Spray Test').locator('..').click();
  await page.getByRole('option', { name: new RegExp(`Salt Spray Test Report ${stamp}`) }).click();
  await shot(page, '03-tests-section-salt-spray');
  await row.getByTestId('vf-save-Salt Spray Test').click();
  await expect(row.getByText('Confirmed')).toBeVisible();
  await shot(page, '04-salt-spray-confirmed');

  const wo = await ok(page.request.post('/api/sequfloh/tracking', { data: { type: 'SaltSpray', siteId: site, number: `SS-${stamp}-2` } }));
  expect(wo.validationForm.fileId).toBeTruthy();
  const copy = await ok(page.request.get(`/api/imported-files/${wo.validationForm.fileId}`));
  expect(copy.blankForm.id).toBe(blank);
  expect(copy.number).toMatch(/-1$/);
  await page.goto(`/sequfloh/record/${wo.id}`);
  await expect(view(page).getByTestId('wo-validation-form')).toContainText('Salt Spray Test form');
  await shot(page, '05-salt-spray-work-order-form');
  await page.goto(`/sequfloh/record/${none.id}`);
  await view(page).getByTestId('wo-attach-form').click();
  await expect(page.getByTestId('attach-form-number')).toHaveValue('');
  await page.getByTestId('attach-form-number').fill(copy.number);
  await page.getByTestId('attach-form-go').click();
  await expect(page.getByTestId('attach-form-error')).toContainText('could not be started');
  await page.getByTestId('attach-form-number').fill('');
  await page.getByTestId('attach-form-go').click();
  await expect(view(page).getByTestId('wo-validation-form')).toContainText(`${copy.number.slice(0, -1)}2`);
  await expect(view(page).getByTestId('wo-attach-form')).toHaveCount(0);
  await ok(page.request.put('/api/sequfloh/validation-forms/tests/salt-spray', { data: { blankFormId: null } }));
});
