import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

test('a Research work order needs a product family or Multiple and a description, and no part number', async ({ page }) => {
  await signIn(page);
  await page.goto('/sequfloh/tracking/new?type=Research');
  await page.getByTestId('create-clock-no').click();
  const v = view(page);
  await expect(v.getByLabel('Part', { exact: true })).toHaveCount(0);
  await expect(v.getByTestId('new-wo-description')).toBeVisible();

  await v.getByRole('combobox', { name: /^Product family/ }).click();
  await page.getByTestId('new-wo-family-multiple').click();
  const yy = String(new Date(new Date().toLocaleString('en-US', { timeZone: 'America/New_York' })).getFullYear() % 100).padStart(2, '0');
  await expect(v.getByTestId('new-wo-number')).toHaveValue(new RegExp(`^WO-RSH-MUL${yy}-\\d{3}$`));

  // the description is required
  await v.getByTestId('new-wo-create').click();
  await expect(v.getByText('Explain clearly what is needed on this Research work order.').first()).toBeVisible();
  await expect(page).toHaveURL(/tracking\/new/);

  await v.getByTestId('new-wo-description').fill('Example pallet of mixed parts: sort and say which can be repurposed.');
  await v.getByTestId('new-wo-create').click();
  await expect(page).not.toHaveURL(/tracking\/new/);
  const rec = view(page);
  await expect(rec.getByText(new RegExp(`WO-RSH-MUL${yy}-\\d{3} — Research work order`)).first()).toBeVisible();
  await expect(rec.getByText('Multiple', { exact: true }).first()).toBeVisible();
  await expect(rec.getByText('Example pallet of mixed parts', { exact: false }).first()).toBeVisible();
});
