import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Ticking Repairable never lists a part under Repair Parts; only the Repair part tick does. */
test('repairable flag is separate from the repair parts list', async ({ page }) => {
  const shots = process.env.E2E_RP_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${name}.png`)); };
  const id = Date.now().toString(36).toUpperCase();
  const make = async (pn: string, name: string, flag: 'Repairable' | 'Repair part') => {
    await page.goto('/sequfloh/parts');
    await view(page).getByRole('button', { name: 'New part' }).click();
    const dlg = page.getByRole('dialog');
    await dlg.getByLabel('Part number').fill(pn);
    await dlg.getByLabel('Name').fill(name);
    const site = dlg.getByLabel('Site');
    if (!(await site.textContent())?.trim().replace('\u200b', '')) { await site.click(); await page.getByRole('option').first().click(); }
    await dlg.getByLabel(flag, { exact: true }).check();
    await expect(dlg.getByTestId('repair-flags-help')).toBeVisible();
    if (flag === 'Repair part') await shot('01-new-part-repair-flags-help');
    await dlg.getByRole('button', { name: 'Save' }).click();
    await expect(dlg).toBeHidden();
  };
  await signIn(page);
  await make(`RPB-${id}`, 'Example gearbox', 'Repairable');
  await make(`RPS-${id}`, 'Example screw M4', 'Repair part');
  await page.goto('/sequfloh/parts?view=repair-parts');
  await expect(view(page).getByText(`RPS-${id}`)).toBeVisible();
  await expect(view(page).getByText(`RPB-${id}`)).toHaveCount(0);
  await expect(view(page).getByText(/Repairable does not add it/)).toBeVisible();
  await shot('02-repair-parts-list');
  await page.goto('/sequfloh/parts?view=repairable');
  await expect(view(page).getByText(`RPB-${id}`)).toBeVisible();
  await expect(view(page).getByText(`RPS-${id}`)).toHaveCount(0);
  await shot('03-repairable-parts-list');
});
