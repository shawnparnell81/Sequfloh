import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

const starters = ['Starter - Validation', 'Starter - Validation / FAI', 'Starter - FAI', 'Starter - Repair Work Instruction', 'Starter - Inspection'];

/** Every instruction type has a starter layout template, and the picker offers it for that type. */
test('starter templates: one per instruction type, offered in the picker', async ({ page }) => {
  const shots = process.env.E2E_TT_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  await signIn(page);
  await page.goto('/sequfloh/instructions/templates');
  const box = view(page).getByRole('region', { name: 'Layout templates' });
  for (const n of starters) await expect(box.getByText(n, { exact: true })).toBeVisible();
  await box.getByText('Starter - FAI', { exact: true }).scrollIntoViewIfNeeded();
  await page.evaluate(() => { const el = [...document.querySelectorAll('[aria-label="Layout templates"] h6, [aria-label="Layout templates"] .MuiTypography-subtitle2')].find((e) => e.textContent === 'Starter - FAI'); el?.scrollIntoView({ block: 'center' }); });
  await shot('01-starter-templates-list');
  await page.goto('/sequfloh/instructions/new?type=Fai');
  const dlg = page.getByRole('dialog').or(view(page)).first();
  const typeSel = dlg.getByLabel('Work instruction type');
  if (!(await typeSel.textContent())?.includes('FAI')) { await typeSel.click(); await page.getByRole('option', { name: 'FAI', exact: true }).click(); }
  const picker = dlg.getByLabel('Start from template');
  await expect(picker).toBeEnabled();
  await picker.click();
  await expect(page.getByRole('option', { name: /^Starter - FAI ·/ })).toBeVisible();
  await expect(page.getByRole('option', { name: /^Starter - Repair Test/ })).toHaveCount(0);
  await shot('02-picker-fai-starter');
  await page.keyboard.press('Escape');
});
