import { expect, test, type Page } from '@playwright/test';
import { signIn, view } from './support';

/** Types like a barcode scanner: a fast burst of keys ending with Enter. */
async function scanKeys(page: Page, code: string) {
  await page.evaluate((c) => {
    const t = document.activeElement ?? document.body;
    for (const key of [...c, 'Enter']) t.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
  }, code);
}

test('a scan opens the part menu, offers to add an unknown part, and never takes ordinary typing', async ({ page }) => {
  await signIn(page);
  const tag = Date.now().toString(36).toUpperCase();
  const pn = `SCN-${tag}`;
  const sites = await page.evaluate(async () => (await (await fetch('/api/organization/sites', { credentials: 'include' })).json()) as { id: string }[]);
  const created = await page.evaluate(async ({ pn, site }) => {
    const r = await fetch('/api/sequfloh/parts', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      part: { partNumber: pn, name: 'Example scan part', description: '', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
      execution: { productFamily: null, assemblyType: null, isRepairable: true, isSerialized: false, isLotControlled: false, drawingNumber: null, specification: null }, typeId: null, attributes: null }) });
    return r.ok;
  }, { pn, site: sites[0]?.id });
  expect(created).toBe(true);

  // a scanner burst with focus on the page opens the part
  await page.locator('body').click({ position: { x: 5, y: 300 } });
  await scanKeys(page, pn.toLowerCase());
  const menu = page.getByTestId('scan-part-menu');
  await expect(menu).toContainText(pn);
  await expect(menu.getByTestId('scan-history')).toBeVisible();
  await expect(menu.getByTestId('scan-drawings')).toBeVisible();
  await expect(menu.getByTestId('scan-new-repair')).toBeVisible();
  await menu.getByTestId('scan-part-info').click();
  await expect(page).toHaveURL(/\/sequfloh\/parts\/[0-9a-f-]{36}/);
  await expect(view(page).getByTestId('part-activity')).toBeVisible();
  await expect(view(page).getByTestId('part-new-repair')).toBeVisible();

  // the Scan box: an unknown number offers to add it, prefilled
  await page.getByTestId('scan-box').fill(`NEW-${tag}`);
  await page.getByTestId('scan-box').press('Enter');
  await expect(page.getByTestId('scan-not-found')).toContainText(`No part found for NEW-${tag}. Add it?`);
  await page.getByTestId('scan-add-part').click();
  await expect(page.getByRole('dialog').getByLabel('Part number', { exact: false }).first()).toHaveValue(`NEW-${tag}`);
  await page.keyboard.press('Escape');

  // typing fast into an ordinary field stays in that field
  await page.getByTestId('scan-box').click();
  await page.getByTestId('scan-box').fill('');
  await page.getByTestId('scan-box').pressSequentially('ABCDEFGH', { delay: 0 });
  await expect(page.getByTestId('scan-box')).toHaveValue('ABCDEFGH');
  await expect(page.getByTestId('scan-dialog')).toHaveCount(0);
});
