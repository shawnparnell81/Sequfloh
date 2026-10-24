import { test, expect } from '@playwright/test';
import path from 'node:path';
import { section, signIn, view } from './support';

/** E2E_XLSX and E2E_DOCX point at local sample files: a workbook whose sheet "Torque Audit" has formulas, and any Word document. */
test.describe('imported files', () => {
  test('an Excel file opens with its formulas and an edit is saved and recalculated', async ({ page }) => {
    test.skip(!process.env.E2E_XLSX, 'set E2E_XLSX');
    await signIn(page);
    await section(page, 'Imported Files');
    await page.getByTestId('imported-files-files-input').setInputFiles(path.resolve(process.env.E2E_XLSX!));
    await page.getByTestId('import-blank').uncheck();
    await page.getByTestId('bulk-import-start').click();
    await expect(page).toHaveURL(/\/sequfloh\/imported-files\/[0-9a-f-]{36}$/);
    const g8 = view(page).locator('td[data-cell="G8"]');
    await expect(g8).toHaveText('FAIL');
    await g8.click();
    await expect(view(page).getByTestId('formula-bar')).toHaveValue(/^=IF\(/);
    await view(page).locator('td[data-cell="F8"]').dblclick();
    await page.keyboard.press('Control+A'); await page.keyboard.type('26.1'); await page.keyboard.press('Enter');
    await view(page).getByTestId('save-cells').click();
    await expect(view(page).getByText('Saved as version 2')).toBeVisible();
    await expect(g8).toHaveText('PASS');
  });

  test('a Word file opens as a page and a paragraph edit is saved', async ({ page }) => {
    test.skip(!process.env.E2E_DOCX, 'set E2E_DOCX');
    await signIn(page);
    await section(page, 'Imported Files');
    await page.getByTestId('imported-files-files-input').setInputFiles(path.resolve(process.env.E2E_DOCX!));
    await page.getByTestId('import-blank').uncheck();
    await page.getByTestId('bulk-import-start').click();
    const para = view(page).locator('p[data-editable]').last();
    await para.click();
    await page.keyboard.press('Control+A'); await page.keyboard.type('Changed in the app.');
    await view(page).getByTestId('document-page').click({ position: { x: 5, y: 5 } });
    await view(page).getByTestId('save-document').click();
    await expect(view(page).getByText('Saved as version 2')).toBeVisible();
    await expect(view(page).getByTestId('document-page')).toContainText('Changed in the app.');
  });
});
