import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { more, section, settledShot, signIn, view } from './support';

/** Fix list 4: the company's own numbers on every record, Salt Spray Test, one Validation / FAI and Repair / Rework. Made-up records only. */
const shots = process.env.FL4_SHOTS;
const shot = async (page: Page, name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };

test('a work order takes the number typed in, can be renumbered later, and a repeat number is refused', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  await page.goto('/sequfloh/tracking/new?type=Inspection');
  await page.getByTestId('create-clock-no').click();
  await view(page).getByLabel('Site').click(); await page.getByRole('option').first().click();
  await view(page).getByLabel('Inspection sub-type').click(); await page.getByRole('option', { name: 'AQL' }).click();
  await view(page).getByTestId('new-wo-description').fill('Made-up housing check');
  await view(page).getByTestId('new-wo-create').click();
  await expect(view(page).getByText('Enter the number.')).toBeVisible();
  const own = `QC/24-${stamp} rev-A`;
  await view(page).getByTestId('new-wo-number').fill(own);
  await shot(page, '01-new-work-order-own-number');
  await view(page).getByTestId('new-wo-create').click();
  await expect(page).toHaveURL(/sequfloh\/record\//);
  await expect(view(page).locator('h1')).toContainText(own);

  // renumber through Edit number: audited on the server, shown at once
  const menu = await more(page);
  await menu.getByTestId('edit-number').click();
  const dlg = page.getByTestId('edit-number-dialog');
  await dlg.getByTestId('record-number').fill(`${own} B`);
  await shot(page, '02-edit-number-dialog');
  await dlg.getByTestId('edit-number-save').click();
  await expect(dlg).toBeHidden();
  await expect(view(page).locator('h1')).toContainText(`${own} B`);

  // the same number again, in another case, is refused for the same type of record
  const site = (await ok(page.request.get('/api/organization/sites')) as { id: string }[])[0].id;
  const dup = await page.request.post('/api/sequfloh/tracking', { data: { number: `${own} b`.toUpperCase(), type: 'Inspection', siteId: site, inspectionSubType: 'AQL' } });
  expect(dup.status()).toBe(400);
  expect(await dup.text()).toContain('already exists');
});

test('Salt Spray Test: its own Work Orders tab, New work order button and type in every type list', async ({ page }) => {
  await signIn(page);
  await section(page, 'Work Orders');
  await view(page).getByTestId('section-page').filter({ hasText: /^Salt Spray Test$/ }).click();
  await expect(page).toHaveURL(/\/sequfloh\/tracking\/salt-spray/);
  await expect(view(page).locator('h1')).toContainText('Salt Spray Test');
  await shot(page, '03-salt-spray-tab');
  await view(page).getByTestId('new-work-order').click();
  await expect(page).toHaveURL(/type=SaltSpray/);
  await page.getByTestId('create-clock-no').click();
  await view(page).getByLabel('Site').click(); await page.getByRole('option').first().click();
  const n = `SST-${Date.now()}`;
  await view(page).getByTestId('new-wo-number').fill(n);
  await shot(page, '04-new-salt-spray-work-order');
  await view(page).getByTestId('new-wo-create').click();
  await expect(page).toHaveURL(/sequfloh\/record\//);
  await expect(view(page)).toContainText('Salt Spray Test');

  // the open list type filter and the Create Report type filter both offer it
  await page.goto('/sequfloh/tracking/open');
  await view(page).getByLabel('Type').click();
  await expect(page.getByRole('option', { name: 'Salt Spray Test' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Validation / FAI' })).toHaveCount(1);
  await expect(page.getByRole('option', { name: 'Repair / Rework' })).toBeVisible();
  await shot(page, '05-type-filter');
  await page.keyboard.press('Escape');
  await page.goto('/sequfloh/chargebacks/new');
  const form = view(page).getByTestId('chargeback-form');
  await form.getByTestId('cb-period-kind').locator('xpath=..').getByRole('combobox').click();
  await page.getByRole('option', { name: 'Week', exact: true }).click();
  await form.getByTestId('cb-filter-type').locator('xpath=..').getByRole('combobox').click();
  await expect(page.getByRole('option', { name: 'Salt Spray Test' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'FAI', exact: true })).toHaveCount(0);
  await shot(page, '06-create-report-type-filter');
  await page.keyboard.press('Escape');
});

test('one Validation / FAI and Repair / Rework in the menus; old links open the combined pages', async ({ page }) => {
  await signIn(page);
  await section(page, 'WI / Procedures');
  const pages = view(page).getByTestId('section-page');
  await expect(pages.filter({ hasText: /^Validation \/ FAI Work Instructions$/ })).toHaveCount(1);
  await expect(pages.filter({ hasText: /^Repair \/ Rework Work Instructions$/ })).toHaveCount(1);
  await expect(pages.filter({ hasText: /^FAI Work Instructions$/ })).toHaveCount(0);
  await shot(page, '00-work-instructions-section');
  for (const [from, to] of [['/sequfloh/tracking/fai', '/sequfloh/tracking/validation-fai'], ['/sequfloh/tracking/validation', '/sequfloh/tracking/validation-fai'],
    ['/sequfloh/instructions/fai', '/sequfloh/instructions/validation'], ['/sequfloh/reports/fai-status', '/sequfloh/reports/validation-status'], ['/sequfloh/settings/fai', '/sequfloh/settings/validation']]) {
    await page.goto(from);
    await expect(page).toHaveURL(new RegExp(`${to}$`));
  }
  await expect(view(page).locator('h1')).toContainText('Validation / FAI Settings');
  await shot(page, '07-validation-fai-settings');
  await page.goto('/sequfloh/reports/validation-status');
  await expect(view(page).locator('h1')).toContainText('Validation / FAI Status');
  await shot(page, '08-validation-fai-status-report');
  await page.goto('/sequfloh/tracking/repair');
  await expect(view(page).locator('h1')).toContainText('Repair / Rework');
  await page.goto('/sequfloh/reports/repair-status');
  await expect(view(page).locator('h1')).toContainText('Repair / Rework Status');
  await page.goto('/sequfloh/tracking/production');
  await expect(view(page).getByTestId('new-work-order')).toBeVisible(); // Production stays reachable
  await page.goto('/sequfloh/tracking/new');
  await page.getByTestId('create-clock-no').click();
  await view(page).getByLabel('Work order type').click();
  await expect(page.getByRole('option')).toHaveText(['Validation / FAI', 'Repair / Rework', 'Inspection', 'Warranty', 'Salt Spray Test', 'Development']);
  await shot(page, '09-new-work-order-type-list');
  await page.keyboard.press('Escape');
  await page.goto('/sequfloh/settings/numbering');
  await expect(view(page).locator('h1')).toContainText('Revision Settings');
  await expect(view(page)).not.toContainText(/sequence/i);
  await shot(page, '10-revision-settings');
});
