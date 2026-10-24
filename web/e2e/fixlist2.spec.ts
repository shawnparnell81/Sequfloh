import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

const shots = process.env.FL2_SHOTS;
const shot = async (page: Page, name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
const pick = async (page: Page, label: string, option: string | RegExp) => {
  await view(page).getByLabel(label, { exact: true }).click();
  await page.getByRole('option', { name: option }).first().click();
};

test('form editor: fit to width, zoom, full screen and open in a new window', async ({ page, context }) => {
  await signIn(page);
  await section(page, 'Imported Files');
  await page.getByTestId('imported-files-files-input').setInputFiles(path.resolve(process.env.E2E_CF_XLSX!));
  await page.getByLabel('Title').fill(`Zoom check ${Date.now()}`);
  await page.getByTestId('bulk-import-start').click();
  await expect(page).toHaveURL(/\/sequfloh\/imported-files\/[0-9a-f-]{36}$/);
  await expect(view(page).getByTestId('zoom-fit')).toHaveClass(/contained/);
  const level = view(page).getByTestId('zoom-level');
  const before = await level.textContent();
  await view(page).getByTestId('zoom-in').click();
  await expect(level).not.toHaveText(before!);
  await view(page).getByTestId('zoom-fit').click();
  await expect(level).toHaveText(before!);
  await view(page).getByTestId('form-fullscreen').click();
  await expect(page.getByTestId('form-fullscreen')).toHaveText('Exit full screen');
  await shot(page, 'form-editor-full-width');
  await page.keyboard.press('Escape');
  await expect(view(page).getByTestId('form-fullscreen')).toHaveText('Full screen');
  const [win] = await Promise.all([context.waitForEvent('page'), view(page).getByTestId('form-new-window').click()]);
  await expect(win.getByTestId('form-window')).toBeVisible();
  await expect(win.getByTestId('form-zoom')).toBeVisible();
  await win.close();
});

test('validation forms by product, and the work order builder for each type', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString().slice(-6);
  const title = `Validation blank ${stamp}`;
  await section(page, 'Imported Files');
  await page.getByTestId('imported-files-files-input').setInputFiles(path.resolve(process.env.E2E_CF_XLSX!));
  await page.getByLabel('Title').fill(title);
  await expect(page.getByTestId('import-blank')).toBeChecked();
  await page.getByTestId('bulk-import-start').click();
  await expect(page).toHaveURL(/\/sequfloh\/imported-files\/[0-9a-f-]{36}$/);

  // confirm the blank for a product family
  const vf = await (await page.request.get('/api/sequfloh/validation-forms')).json();
  const fam: string = vf.families[0].label;
  await page.goto('/sequfloh/settings/validation-forms');
  await expect(view(page).getByTestId('validation-forms-table')).toBeVisible();
  const row = view(page).getByTestId(`vf-row-${fam}`);
  await row.getByLabel('Blank form').click();
  await page.getByRole('option', { name: new RegExp(title) }).click();
  await row.getByTestId(`vf-save-${fam}`).click();
  await expect(row.getByText('Confirmed')).toBeVisible();
  await shot(page, 'settings-validation-forms-by-product');

  // validation: family attaches a copy of the confirmed form
  await page.goto('/sequfloh/tracking/new?type=Validation');
  await pick(page, 'Site', /./);
  await pick(page, 'Product family', fam);
  await expect(view(page).getByTestId('new-wo-form-note')).toContainText(title);
  await view(page).getByTestId('new-wo-qty').fill('3');
  await shot(page, 'new-work-order-validation');
  await view(page).getByTestId('new-wo-create').click();
  await expect(view(page).getByTestId('wo-validation-form')).toContainText(`${fam} validation`);

  // repair: the test results block
  await page.goto('/sequfloh/tracking/new?type=Repair');
  await pick(page, 'Site', /./);
  await view(page).getByLabel('Repairable part', { exact: true }).click();
  await view(page).getByLabel('Repairable part', { exact: true }).pressSequentially('3', { delay: 50 });
  await page.getByRole('option').first().click();
  await shot(page, 'new-work-order-repair');
  await view(page).getByTestId('new-wo-create').click();
  await expect(view(page).getByTestId('wo-test-results')).toContainText('Test results');

  // inspection and warranty: sub-type, description, results block with quarantine reason
  for (const t of ['Inspection', 'Warranty']) {
    await page.goto(`/sequfloh/tracking/new?type=${t}`);
    await pick(page, 'Site', /./);
    await pick(page, 'Inspection sub-type', 'AQL');
    await view(page).getByTestId('new-wo-description').fill(`Made-up ${t.toLowerCase()} check`);
    await view(page).getByTestId('new-wo-qty').fill('10');
    await shot(page, `new-work-order-${t.toLowerCase()}`);
    await view(page).getByTestId('new-wo-create').click();
    const box = view(page).getByTestId('wo-inspection-results');
    await expect(box).toBeVisible();
    await box.getByTestId('insp-good').fill('8');
    await box.getByTestId('insp-bad').fill('2');
    await box.getByLabel('Quarantine').click();
    await page.getByRole('option', { name: 'Yes' }).click();
    await box.getByTestId('insp-save').click();
    await expect(box.getByRole('alert')).toContainText('why');
    await box.getByTestId('insp-why').fill('Made-up burrs on the edge');
    await box.getByTestId('insp-save').click();
    await expect(box.getByText('Inspection results saved.')).toBeVisible();
    if (t === 'Inspection') await shot(page, 'inspection-results-block');
  }

  // the sidebar and folder offer the new work order button
  await page.goto('/sequfloh/tracking/open');
  await expect(view(page).getByTestId('new-work-order')).toBeVisible();
});

test('part dropdowns list nothing until you type, then search every number and show the part number first', async ({ page }) => {
  await signIn(page);
  const all = (await (await page.request.get('/api/sequfloh/parts?pageSize=200&view=active')).json()).items as { partNumber: string; name: string; description?: string; attributes?: string }[];
  const p = all.find((x) => x.attributes && JSON.parse(x.attributes).jhPn);
  expect(p, 'a part with a JH PN').toBeTruthy();
  const jh: string = JSON.parse(p!.attributes!).jhPn;
  await page.goto('/sequfloh/tracking/new?type=Inspection');
  const box = view(page).getByLabel('Part', { exact: true });
  await box.click();
  await expect(page.getByTestId('part-option')).toHaveCount(0);
  await expect(page.getByText('Type a part number, JH PN, DWG #, NAPA or DMA')).toBeVisible();
  await box.pressSequentially(jh, { delay: 20 });
  const opt = page.getByTestId('part-option').filter({ hasText: p!.partNumber }).first();
  await expect(opt).toBeVisible();
  await expect(opt.locator('p, span').first()).toHaveText(new RegExp(`^${p!.partNumber.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`));
  await shot(page, 'part-dropdown-search');
});
