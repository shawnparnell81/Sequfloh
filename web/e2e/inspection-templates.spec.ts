import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

const names = ['AQL Sample Result', 'Product Alert', 'Validation Result', 'Warranty Claim Return', 'Supplier Request', 'Customer Request / Complaint', 'Random Inspection', 'Recall / Containment Sort'].map((n) => `Inspection - ${n}`);

/** The standard inspection layout templates are listed and open with their steps. */
test('inspection templates: listed on the templates page and open with their steps', async ({ page }) => {
  const shots = process.env.E2E_TPL_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  await signIn(page);
  await page.goto('/sequfloh/instructions/templates');
  const box = view(page).getByRole('region', { name: 'Layout templates' });
  for (const n of names) await expect(box.getByText(n, { exact: true })).toBeVisible();
  await box.scrollIntoViewIfNeeded();
  await page.evaluate(() => document.querySelector('[aria-label="Layout templates"]')?.scrollIntoView({ block: 'start' }));
  await shot('01-inspection-templates-list');
  await box.getByRole('button', { name: 'Edit Inspection - Recall / Containment Sort' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.locator('input[value="Inspect 100%"]')).toHaveCount(1);
  await expect(dlg.locator('input[value="Segregate counts"]')).toHaveCount(1);
  await shot('02-recall-containment-template-open');
  await dlg.getByRole('button', { name: 'Cancel' }).click();
});
