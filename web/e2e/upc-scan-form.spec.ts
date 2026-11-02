import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** A made-up UPC-A with a right check digit. */
function upc() {
  const body = '3' + Array.from({ length: 10 }, () => Math.floor(Math.random() * 10)).join('');
  const sum = [...body].reverse().reduce((s, d, i) => s + Number(d) * (i % 2 === 0 ? 3 : 1), 0);
  return body + ((10 - (sum % 10)) % 10);
}

test('Scan UPC on a filled validation form fills the part details, and an unknown UPC offers to type the part number', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, name)); };
  await page.setViewportSize({ width: 1440, height: 900 });
  await signIn(page);
  const tag = Date.now().toString(36).toUpperCase();
  const pn = `EX-${tag}`;
  const code = upc();
  const bytes = [...fs.readFileSync(path.resolve('e2e/example-validation-form.xlsx'))];
  const fileId = await page.evaluate(async ({ pn, code, tag, bytes }) => {
    const j = async (url: string, init?: RequestInit) => { const r = await fetch(url, init); if (!r.ok) throw new Error(`${url}: ${r.status} ${await r.text()}`); return r.json(); };
    const site = (await j('/api/organization/sites'))[0].id;
    const fams = await j('/api/sequfloh/reference/product-families') as { id: string; label: string }[];
    const fam = fams.find((f) => f.label === 'Struts') ?? fams[0];
    await j('/api/sequfloh/parts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
      part: { partNumber: pn, name: fam.label, description: 'Example rear strut', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
      execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: `DW-${tag}`, specification: null }, typeId: fam.id, attributes: null, upc: code }) });
    const form = new FormData();
    form.append('file', new Blob([new Uint8Array(bytes)]), `Example validation form ${tag}.xlsx`);
    form.append('siteId', site); form.append('blankForm', 'true'); form.append('number', `EVF-${tag}`);
    const blank = await j('/api/imported-files', { method: 'POST', body: form });
    const copy = await j(`/api/imported-files/${blank.id}/fill-out`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ number: `EVF-${tag}-1`, title: 'Example validation form – unit 1' }) });
    return copy.id as string;
  }, { pn, code, tag, bytes });

  await page.goto(`/sequfloh/imported-files/${fileId}`);
  const bar = view(page).getByTestId('upc-scan-bar');
  await expect(bar).toBeVisible();
  await expect(view(page).locator('td[data-cell="A3"]')).toHaveText('Part No.:');
  await shot('01-scan-upc-field.png');

  // an unknown UPC: nothing is filled, and the part number can be typed instead
  const unknown = upc();
  await view(page).getByTestId('upc-scan').fill(unknown);
  await view(page).getByTestId('upc-scan').press('Enter');
  await expect(view(page).getByTestId('upc-scan-result')).toContainText(`No part has UPC ${unknown}.`);
  await expect(view(page).getByTestId('upc-type-pn')).toBeVisible();
  await shot('03-unknown-upc.png');
  await view(page).getByTestId('upc-type-pn').click();
  await expect(view(page).getByTestId('upc-pn')).toBeVisible();
  await view(page).getByRole('button', { name: 'Cancel' }).click();

  // a scanner types the code and Enter into the field
  await view(page).getByTestId('upc-scan').click();
  await page.keyboard.type(code, { delay: 5 });
  await page.keyboard.press('Enter');
  await expect(view(page).getByTestId('upc-scan-result')).toContainText(`Part ${pn}`);
  await expect(view(page).locator('td[data-cell="B3"]')).toHaveText(pn);
  await expect(view(page).locator('td[data-cell="B4"]')).toHaveText('Example rear strut');
  await expect(view(page).locator('td[data-cell="B6"]')).toHaveText(`DW-${tag}`);
  await expect(view(page).locator('td[data-cell="B7"]')).toHaveText(code);
  await expect(view(page).locator('td[data-cell="B8"]')).toHaveText('');
  await shot('02-filled-after-scan.png');
  await view(page).getByTestId('save-cells').click();
  await expect(view(page).getByText(/Saved as version 2/)).toBeVisible();
  const detail = await page.evaluate(async (id) => (await fetch(`/api/imported-files/${id}`)).json(), fileId);
  expect(detail.partNumber).toBe(pn);
});
