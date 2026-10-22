import path from 'node:path';
import { expect, test } from '@playwright/test';
import { section, signIn, view } from './support';

/** E2E_CF_XLSX points at a workbook whose "Torque Audit" sheet has PASS/FAIL formulas in G5:G8 coloured green/red by conditional formatting. */
test('pass and fail colours change as soon as a reading is edited', async ({ page }) => {
  test.skip(!process.env.E2E_CF_XLSX, 'set E2E_CF_XLSX');
  const shots = process.env.E2E_SHOTS;
  await signIn(page);
  await section(page, 'Imported Files');
  await view(page).getByTestId('import-file').click();
  await page.getByTestId('import-input').setInputFiles(path.resolve(process.env.E2E_CF_XLSX!));
  await page.getByTestId('import-blank').uncheck();
  await page.getByTestId('import-upload').click();
  await expect(page).toHaveURL(/\/sequfloh\/imported-files\/[0-9a-f-]{36}$/);
  const g6 = view(page).locator('td[data-cell="G6"]');
  await expect(g6).toHaveText('FAIL');
  await expect(g6).toHaveAttribute('data-fill', '#FFC7CE');
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveAttribute('data-fill', '#C6EFCE');
  if (shots) await page.screenshot({ path: path.join(shots, '01-imported-pass-and-fail.png') });
  await view(page).locator('td[data-cell="F6"]').dblclick();
  await page.keyboard.press('Control+A'); await page.keyboard.type('26.4'); await page.keyboard.press('Enter');
  await expect(g6).toHaveText('PASS');
  await expect(g6).toHaveAttribute('data-fill', '#C6EFCE');
  if (shots) await page.screenshot({ path: path.join(shots, '02-edited-fail-turns-green.png') });
  await view(page).locator('td[data-cell="F5"]').dblclick();
  await page.keyboard.press('Control+A'); await page.keyboard.type('31.5'); await page.keyboard.press('Enter');
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveText('FAIL');
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveAttribute('data-fill', '#FFC7CE');
  if (shots) await page.screenshot({ path: path.join(shots, '03-edited-pass-turns-red.png') });
  await view(page).getByTestId('save-cells').click();
  await expect(view(page).getByText('Saved as version 2')).toBeVisible();
  await expect(g6).toHaveAttribute('data-fill', '#C6EFCE');
  await expect(view(page).locator('td[data-cell="G5"]')).toHaveAttribute('data-fill', '#FFC7CE');
  if (shots) await page.screenshot({ path: path.join(shots, '04-saved-version-2.png') });
});
