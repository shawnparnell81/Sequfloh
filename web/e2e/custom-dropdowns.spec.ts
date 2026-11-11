import path from 'node:path';
import { expect, test } from '@playwright/test';
import { choosePartName, choosePartType, settledShot, signIn, view } from './support';

const shots = '/opt/cursor/artifacts';

/** An administrator adds a dropdown, puts it on parts, filters by it, hides it, and confirms delete. */
test('custom dropdown on parts: create, filter, hide, and confirm delete', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1100 });
  const k = Date.now().toString(36).toUpperCase();
  const name = `Finish ${k}`;
  const pn = `CD-${k}`;
  await signIn(page);
  const v = view(page);

  await page.goto('/sequfloh/admin/reference');
  await v.getByTestId('new-dropdown').click();
  const dlg = page.getByTestId('new-dropdown-dialog');
  await dlg.getByTestId('dropdown-name').fill(name);
  await dlg.getByTestId('dropdown-description').fill('Example finish chosen on a part.');
  await dlg.getByTestId('dropdown-value-0').fill('Gloss');
  await dlg.getByTestId('dropdown-add-value').click();
  await dlg.getByTestId('dropdown-value-1').fill('Matte');
  await expect(dlg.getByTestId('place-parts')).toBeVisible();
  await dlg.getByTestId('place-parts').check();
  await expect(dlg.getByText('Show in list').first()).toBeVisible();
  await expect(dlg.getByText('Work orders · Repair / Rework')).toBeVisible();
  await expect(dlg.getByText('Projects · Projects')).toBeVisible();
  await dlg.getByTestId('place-parts').evaluate((el) => el.scrollIntoView({ block: 'center' }));
  await settledShot(page, path.join(shots, 'screenshot_new_dropdown_placements.png'));
  await dlg.getByTestId('dropdown-save').click();
  await expect(dlg).toBeHidden();
  await expect(v.getByTestId(`custom-dropdown-${name}`)).toBeVisible();

  await page.goto('/sequfloh/parts');
  await v.getByRole('button', { name: 'New part' }).click();
  const part = page.getByRole('dialog');
  await part.getByLabel(/^Customer part #|^Part number/).fill(pn);
  await choosePartType(page, 'Fuel Pump');
  await choosePartName(page, 'Fuel Pump');
  const site = part.getByRole('combobox', { name: 'Site' });
  await site.click();
  await page.getByRole('option').first().click();
  const finish = part.getByRole('combobox', { name });
  await expect(finish).toBeVisible();
  await finish.click();
  await page.getByRole('option', { name: 'Gloss', exact: true }).click();
  await finish.evaluate((el) => el.scrollIntoView({ block: 'center' }));
  await settledShot(page, path.join(shots, 'screenshot_custom_field_part_form.png'));
  await part.getByRole('button', { name: 'Save' }).click();
  await expect(part).toBeHidden();
  await expect(v.getByTestId(`custom-value-${name}`)).toContainText('Gloss');

  await page.goto(`/sequfloh/parts?q=${pn}`);
  await expect(v.getByRole('columnheader', { name })).toBeVisible();
  await expect(v.getByTestId(`custom-col-${name}`)).toHaveText('Gloss');
  await v.getByRole('combobox', { name }).click();
  await page.getByRole('option', { name: 'Gloss', exact: true }).click();
  await expect(v.getByTestId(`custom-col-${name}`)).toHaveText('Gloss');
  await v.getByTestId(`custom-col-${name}`).evaluate((el) => el.scrollIntoView({ inline: 'center', block: 'center' }));
  await settledShot(page, path.join(shots, 'screenshot_custom_field_list_filter.png'));
  await v.getByRole('combobox', { name }).click();
  await page.getByRole('option', { name: 'Matte', exact: true }).click();
  await expect(v.getByText('No parts match these filters.')).toBeVisible();

  await page.goto('/sequfloh/admin/reference');
  await v.getByTestId(`custom-dropdown-${name}`).getByRole('button', { name: 'Edit' }).click();
  const edit = page.getByTestId('new-dropdown-dialog');
  await edit.getByTestId('place-parts').uncheck();
  await edit.getByTestId('dropdown-save').click();
  await expect(edit).toBeHidden();

  await page.goto(`/sequfloh/parts?q=${pn}`);
  await expect(v.getByRole('columnheader', { name })).toHaveCount(0);
  await v.getByText(pn, { exact: true }).click();
  await v.getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('dialog').getByRole('combobox', { name })).toHaveCount(0);
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  await page.goto('/sequfloh/admin/reference');
  await v.getByTestId(`custom-dropdown-${name}`).getByRole('button', { name: 'Edit' }).click();
  const del = page.getByTestId('new-dropdown-dialog');
  await expect(del.getByTestId('delete-dropdown-box')).toContainText('Used on 1 record');
  await expect(del.getByTestId('delete-dropdown-box')).toContainText(name);
  await del.getByTestId('delete-dropdown-confirm').fill(name);
  await del.getByTestId('delete-dropdown-box').scrollIntoViewIfNeeded();
  await settledShot(page, path.join(shots, 'screenshot_custom_dropdown_delete.png'));
  await del.getByTestId('delete-dropdown').click();
  await expect(del).toBeHidden();
  await expect(v.getByTestId(`custom-dropdown-${name}`)).toHaveCount(0);
});
