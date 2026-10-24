import path from 'node:path';
import { expect, test } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** Uses the E2E_CF_XLSX test workbook ("Torque Audit": readings in F5:F8, PASS/FAIL in G5:G8 coloured by conditional formatting). */
test('a blank form stays blank; filled copies and saved copies are separate files linked to it', async ({ page }) => {
  test.skip(!process.env.E2E_CF_XLSX, 'set E2E_CF_XLSX');
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, name)); };
  const stamp = Date.now().toString().slice(-6);
  const blankTitle = `Torque audit blank ${stamp}`;
  await signIn(page);
  await section(page, 'Imported Files');
  await page.getByTestId('imported-files-files-input').setInputFiles(path.resolve(process.env.E2E_CF_XLSX!));
  await page.getByLabel('Title').fill(blankTitle);
  await expect(page.getByTestId('import-blank')).toBeChecked();
  await shot('01-import-keep-as-blank.png');
  await page.getByTestId('bulk-import-start').click();
  await expect(page).toHaveURL(/\/sequfloh\/imported-files\/[0-9a-f-]{36}$/);
  const blankUrl = page.url();

  // the blank is protected: no save button, double-click does not open an editor
  await expect(view(page).getByTestId('blank-chip')).toBeVisible();
  await expect(view(page).getByTestId('save-cells')).toHaveCount(0);
  await view(page).locator('td[data-cell="F6"]').dblclick();
  await expect(view(page).getByLabel('Edit F6')).toHaveCount(0);
  await shot('02-blank-form-protected.png');

  // fill out a copy
  await view(page).getByTestId('fill-out').click();
  await expect(page.getByTestId('copy-title')).toHaveValue(new RegExp(`^${blankTitle} – \\d{4}-\\d{2}-\\d{2}$`));
  await page.getByTestId('copy-title').fill(`${blankTitle} – line 2`);
  await shot('03-fill-out-a-copy.png');
  await page.getByTestId('copy-confirm').click();
  await expect(view(page).getByTestId('blank-link')).toContainText(blankTitle);
  expect(page.url()).not.toBe(blankUrl);
  const g6 = view(page).locator('td[data-cell="G6"]');
  await view(page).locator('td[data-cell="F6"]').dblclick();
  await page.keyboard.press('Control+A'); await page.keyboard.type('26.4'); await page.keyboard.press('Enter');
  await expect(g6).toHaveText('PASS');
  await expect(g6).toHaveAttribute('data-fill', '#C6EFCE');
  await shot('04-filled-copy-editing-live-colours.png');
  await view(page).getByTestId('save-cells').click();
  await expect(view(page).getByText('Saved as version 2')).toBeVisible();
  const filledUrl = page.url();

  // save as new copy: the filled copy stays as it is
  await view(page).locator('td[data-cell="F5"]').dblclick();
  await page.keyboard.press('Control+A'); await page.keyboard.type('31.5'); await page.keyboard.press('Enter');
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveAttribute('data-fill', '#FFC7CE');
  await view(page).getByTestId('save-as-copy').click();
  await page.getByTestId('copy-title').fill(`${blankTitle} – retest`);
  await shot('05-save-as-new-copy.png');
  await page.getByTestId('copy-confirm').click();
  await expect(page).not.toHaveURL(filledUrl);
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveText('FAIL');
  await expect(view(page).locator('td[data-cell="G6"]')).toHaveText('PASS');
  await expect(view(page).getByTestId('blank-link')).toContainText(blankTitle);
  await shot('06-new-copy-opened.png');
  await page.goto(filledUrl);
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveText('PASS');
  await expect(view(page).locator('td[data-cell="G6"]')).toHaveText('PASS');

  // the blank lists its copies newest first and is still blank
  await page.goto(blankUrl);
  await expect(view(page).locator('td[data-cell="G6"]')).toHaveText('FAIL');
  await view(page).getByRole('tab', { name: /Filled copies \(2\)/ }).click();
  const rows = view(page).getByTestId('copies').locator('> div');
  await expect(rows.nth(0)).toContainText('retest');
  await expect(rows.nth(1)).toContainText('line 2');
  await shot('07-blank-lists-filled-copies.png');

  // Edit blank turns editing on for the blank itself
  await view(page).getByRole('tab', { name: 'Sheet' }).click();
  await view(page).getByRole('switch', { name: 'Edit blank' }).check();
  await expect(view(page).getByTestId('save-cells')).toBeVisible();
  await expect(view(page).getByTestId('upload-version')).toBeVisible();
  await shot('08-edit-blank-on.png');
  await view(page).getByRole('switch', { name: 'Edit blank' }).uncheck();

  await view(page).getByRole('tab', { name: 'History' }).click();
  await expect(view(page).getByText('Created from blank form').first()).toBeVisible();
  await expect(view(page).getByText('Saved as copy').first()).toBeVisible();
  await shot('09-blank-history-audit.png');

  await page.goto('/sequfloh/imported-files?kind=blank');
  await expect(view(page).getByText(blankTitle, { exact: true })).toBeVisible();
  await shot('10-list-blank-forms.png');
  await page.goto('/sequfloh/imported-files?kind=filled');
  await expect(view(page).getByText(`${blankTitle} – retest`)).toBeVisible();
  await expect(view(page).getByText(blankTitle, { exact: true })).toHaveCount(0);
  await shot('11-list-filled-forms.png');
});
