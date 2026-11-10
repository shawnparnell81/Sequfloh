import { expect, test } from '@playwright/test';
import { chooseImportScope, settledShot, signIn, view } from './support';

const shots = '/opt/cursor/artifacts';

/** Customer and product line on Part Master import. A shared drawing is not a conflict. A different drawing on the same part is held. */
test('import by customer and product line, hold a drawing conflict, leave a shared drawing', async ({ page }) => {
  const shot = (name: string) => settledShot(page, `${shots}/${name}.png`);
  const k = Date.now().toString(36).toUpperCase();
  const fl = `FL-${k}`;
  const fr = `FR-${k}`;
  const added = `NW-${k}`;
  await signIn(page);
  await page.goto('/sequfloh/parts');
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByTestId('import-customer')).toBeVisible();
  await expect(dlg.getByTestId('import-line')).toBeVisible();
  await shot('screenshot_import_customer_and_line');

  const first = ['Part number,Description,Drawing number,Position',
    `${fl},Example left coil,9214-0800,FL`,
    `${fr},Example right coil,9214-0800,FR`].join('\n');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-coil.csv', mimeType: 'text/csv', buffer: Buffer.from(first) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await expect(dlg.getByTestId('scope-required')).toBeVisible();
  await shot('screenshot_import_scope_required');
  await chooseImportScope(page, 'Coil Spring');
  await dlg.getByRole('combobox', { name: /^Name/ }).click();
  await page.getByRole('option', { name: 'Coil Spring', exact: true }).click();
  await expect(dlg.getByRole('button', { name: 'Save layout' })).toBeVisible();
  await expect(dlg.locator('input[data-field="drawingNumber"]')).toHaveValue('col:2');
  await expect(dlg.locator('input[data-field="attr:position"]')).toHaveValue('col:3');
  await shot('screenshot_import_mapping_and_layout');
  await dlg.getByRole('button', { name: 'Preview' }).click();
  const counts = dlg.getByTestId('import-counts');
  await expect(counts).toContainText('Add 2');
  await expect(counts).toContainText('Held 0');
  await counts.scrollIntoViewIfNeeded();
  await shot('screenshot_import_shared_drawing_counts');
  await dlg.getByRole('button', { name: 'Import 2 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('2 added');
  await expect(dlg.getByTestId('import-done')).toContainText('0 held');
  await shot('screenshot_import_confirmed');
  await dlg.getByRole('button', { name: 'Close' }).click();

  // the same customer and line: a different drawing on FL is held; FR is unchanged; a new part may share 9214-0800
  await view(page).getByRole('button', { name: 'Import parts' }).click();
  await chooseImportScope(page, 'Coil Spring');
  const second = ['Part number,Description,Drawing number,Position',
    `${fl},Example left coil,9214-9999,FL`,
    `${fr},Example right coil,9214-0800,FR`,
    `${added},Example shared drawing,9214-0800,FL`].join('\n');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-coil-again.csv', mimeType: 'text/csv', buffer: Buffer.from(second) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await dlg.getByRole('combobox', { name: /^Name/ }).click();
  await page.getByRole('option', { name: 'Coil Spring', exact: true }).click();
  await dlg.getByLabel('Update existing').check();
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(counts).toContainText('Held 1');
  await expect(counts).toContainText('Unchanged 1');
  await expect(counts).toContainText('Add 1');
  await expect(dlg.getByText(`Held ${fl}: saved drawing 9214-0800; file says 9214-9999.`)).toBeVisible();
  await expect(dlg.getByTestId('export-held')).toBeVisible();
  await dlg.getByText(`Held ${fl}: saved drawing 9214-0800; file says 9214-9999.`).scrollIntoViewIfNeeded();
  await shot('screenshot_import_held_row');
  await dlg.getByRole('button', { name: 'Import 1 parts' }).click();
  await expect(dlg.getByTestId('import-done')).toContainText('1 added');
  await expect(dlg.getByTestId('import-done')).toContainText('1 held');
  await dlg.getByRole('button', { name: 'Close' }).click();

  await view(page).getByRole('button', { name: 'Import parts' }).click();
  await chooseImportScope(page, 'Coil Spring');
  await dlg.getByTestId('import-file').setInputFiles({ name: 'example-coil-again.csv', mimeType: 'text/csv', buffer: Buffer.from(second) });
  await dlg.getByRole('button', { name: 'Read file' }).click();
  await dlg.getByRole('combobox', { name: /^Name/ }).click();
  await page.getByRole('option', { name: 'Coil Spring', exact: true }).click();
  await dlg.getByLabel('Update existing').check();
  await dlg.getByRole('button', { name: 'Preview' }).click();
  await expect(counts).toContainText('Add 0');
  await expect(counts).toContainText('Unchanged 2');
  await expect(counts).toContainText('Held 1');
  await expect(dlg.getByText(`Held ${fl}: saved drawing 9214-0800; file says 9214-9999.`)).toBeVisible();
  await dlg.getByText(`Held ${fl}: saved drawing 9214-0800; file says 9214-9999.`).scrollIntoViewIfNeeded();
  await shot('screenshot_import_repeat_held_row');
  await dlg.getByRole('button', { name: 'Cancel' }).click();
});
