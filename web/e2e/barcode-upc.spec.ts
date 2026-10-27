import { expect, test, type Page } from '@playwright/test';
import { signIn, view } from './support';

async function scanKeys(page: Page, code: string) {
  await page.evaluate((c) => {
    const t = document.activeElement ?? document.body;
    for (const key of [...c, 'Enter']) t.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
  }, code);
}
/** A made-up UPC-A with a right check digit. */
function upc() {
  const body = '2' + Array.from({ length: 10 }, () => Math.floor(Math.random() * 10)).join('');
  const sum = [...body].reverse().reduce((s, d, i) => s + Number(d) * (i % 2 === 0 ? 3 : 1), 0);
  return body + ((10 - (sum % 10)) % 10);
}

test('an unknown box-label barcode is linked to a part, and a part takes a barcode from its page', async ({ page }) => {
  await signIn(page);
  const tag = Date.now().toString(36).toUpperCase();
  const pn = `UPC-${tag}`;
  const sites = await page.evaluate(async () => (await (await fetch('/api/organization/sites')).json()) as { id: string }[]);
  const ok = await page.evaluate(async ({ pn, site }) => (await fetch('/api/sequfloh/parts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    part: { partNumber: pn, name: 'Example label part', description: '', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: null, specification: null }, typeId: null, attributes: null }) })).ok, { pn, site: sites[0]?.id });
  expect(ok).toBe(true);

  const code = upc();
  await page.locator('body').click({ position: { x: 5, y: 300 } });
  await scanKeys(page, code);
  await expect(page.getByTestId('scan-unlinked-text')).toContainText(`Barcode ${code} isn't linked to a part. Link to a part?`);
  await expect(page.getByTestId('scan-add-new-part')).toBeVisible();
  await page.getByTestId('scan-link-search').fill(pn);
  await page.getByRole('option', { name: new RegExp(pn) }).click();
  await page.getByTestId('scan-link-part').click();
  const menu = page.getByTestId('scan-part-menu');
  await expect(menu).toContainText(pn);
  await menu.getByTestId('scan-part-info').click();
  await expect(view(page).getByTestId('part-upc-value')).toHaveText(code);

  // Change barcode: a scan into the open dialog goes into its box, not the global scan
  const next = upc();
  await view(page).getByTestId('part-barcode-button').click();
  await page.getByTestId('part-barcode-input').fill('');
  await page.getByTestId('part-barcode-input').pressSequentially(next, { delay: 0 });
  await expect(page.getByTestId('scan-unlinked')).toHaveCount(0);
  await page.getByTestId('part-barcode-input').press('Enter');
  await expect(view(page).getByTestId('part-upc-value')).toHaveText(next);
});
