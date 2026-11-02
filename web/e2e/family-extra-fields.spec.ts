import path from 'node:path';
import { expect, test } from '@playwright/test';
import { choosePartType, settledShot, signIn, view } from './support';

test('an administrator turns on NAPA PN for a product family that does not show it, and the part form shows it', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, name)); };
  await signIn(page);
  const fam = await page.evaluate(async () => ((await (await fetch('/api/sequfloh/reference/product-families')).json()) as { id: string; label: string }[]).find((f) => f.label === 'Air Struts')!);
  try {
    await page.goto('/sequfloh/admin/dropdowns?list=product-families');
    await view(page).getByTestId('dropdown-row').filter({ has: page.getByRole('cell', { name: 'Air Struts', exact: true }) }).getByTestId('family-na-fields').click();
    const dlg = page.getByRole('dialog');
    await expect(dlg.getByTestId('family-extra-fields')).toBeVisible();
    await expect(dlg.getByTestId('family-extra-fields').getByLabel('Show NAPA US #')).toHaveCount(0); // already on this family
    await dlg.getByLabel('Show NAPA PN').check();
    await shot('01-extra-fields-setting.png');
    await dlg.getByRole('button', { name: 'Save' }).click();
    await expect(dlg).toBeHidden();

    await page.goto('/sequfloh/parts');
    await view(page).getByRole('button', { name: 'New part' }).click();
    await choosePartType(page, 'Air Struts');
    const form = page.getByRole('dialog');
    await expect(form.getByLabel('NAPA PN')).toBeVisible();
    await expect(form.getByLabel('NAPA US #')).toBeVisible();
    await expect(form.getByLabel('DMA PN')).toHaveCount(0);
    await shot('02-part-form-with-napa-pn.png');
  } finally {
    await page.evaluate(async (id) => { await fetch(`/api/sequfloh/reference/values/${id}/shown-fields`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fields: [] }) }); }, fam.id);
  }
});
