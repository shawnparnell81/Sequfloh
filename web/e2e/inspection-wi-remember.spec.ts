import path from 'node:path';
import { expect, test } from '@playwright/test';
import { account, settledShot, signIn, tabs, view } from './support';

const shots = process.env.E2E_IWR_SHOTS;
const shot = async (page: import('@playwright/test').Page, name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
const names = ['AQL Sample Result', 'Product Alert', 'Validation Result', 'Warranty Claim Return', 'Supplier Request', 'Customer Request / Complaint', 'Random Inspection', 'Recall / Containment Sort'].map((n) => `Inspection - ${n}`);

test('work instruction type offers Inspection and the inspection templates are filed under it', async ({ page }) => {
  await signIn(page);
  await page.goto('/sequfloh/instructions/new');
  await page.getByLabel('Work instruction type').click();
  await expect(page.getByRole('option', { name: 'Inspection', exact: true })).toBeVisible();
  await shot(page, '01-wi-type-dropdown-inspection');
  await page.getByRole('option', { name: 'Inspection', exact: true }).click();
  const picker = page.getByLabel('Start from template');
  await picker.click();
  await expect(page.getByRole('option', { name: /^Starter - Inspection/ })).toBeVisible();
  await expect(page.getByRole('option', { name: /^Inspection - AQL Sample Result/ })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.goto('/sequfloh/instructions/templates');
  const box = view(page).getByRole('region', { name: 'Layout templates' });
  for (const n of names) await expect(box.getByText(n, { exact: true })).toBeVisible();
  await expect(box.getByText(/^Inspection · /)).toHaveCount(9);
  await page.evaluate(() => document.querySelector('[aria-label="Layout templates"]')?.scrollIntoView({ block: 'start' }));
  await shot(page, '02-templates-under-inspection');
});

test('remember me keeps a lasting sign-in cookie and still starts with Home only', async ({ page }) => {
  const { email, password } = account();
  await page.goto('/');
  const remember = page.getByLabel('Remember me for 30 days on this device');
  await expect(remember).not.toBeChecked();
  await page.fill('input[type=email]', email);
  await remember.check();
  await shot(page, '03-login-remember-me');
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await expect(page.getByTestId('tab-strip')).toBeVisible();
  await expect(tabs(page)).toHaveCount(1);
  const c = (await page.context().cookies()).find((x) => x.name === 'sqf.session');
  const days = ((c?.expires ?? 0) - Date.now() / 1000) / 86400;
  expect(days).toBeGreaterThan(29.9);
  expect(days).toBeLessThan(30.1);
  await page.getByTestId('user-menu').click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.locator('input[type=password]')).toBeVisible();
  expect((await page.context().cookies()).some((x) => x.name === 'sqf.session' && x.value)).toBe(false);
});
