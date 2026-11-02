import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

/** Part Master search finds a part by any number on it: OR #, NAPA PN, Cross, JH PN, DWG # and UPC. Made-up numbers only. */
test('part master search finds a part by any of its numbers', async ({ page }) => {
  await signIn(page);
  const k = Date.now().toString(36).toUpperCase();
  const body = '2' + Array.from({ length: 10 }, () => Math.floor(Math.random() * 10)).join('');
  const sum = [...body].reverse().reduce((s, d, i) => s + Number(d) * (i % 2 === 0 ? 3 : 1), 0);
  const upc = body + ((10 - (sum % 10)) % 10);
  const sites = await page.evaluate(async () => (await (await fetch('/api/organization/sites')).json()) as { id: string }[]);
  const ok = await page.evaluate(async ({ k, upc, site }) => (await fetch('/api/sequfloh/parts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
    part: { partNumber: `AN-${k}`, name: 'Example search part', description: '', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: `DW${k}`, specification: null },
    typeId: null, attributes: { orNumber: `OR${k}`, napaPn: `NP${k}`, cross: `CR${k}`, jhPn: `JH${k}` }, upc }) })).ok, { k, upc, site: sites[0]?.id });
  expect(ok).toBe(true);

  await page.goto('/sequfloh/parts');
  const v = view(page);
  for (const q of [`np${k.toLowerCase()}`, `OR${k}`, `cr${k}`, `JH${k}`, `DW${k}`, upc.slice(1, 9)]) {
    await v.getByTestId('parts-search').fill(q);
    await v.getByRole('button', { name: 'Search', exact: true }).click();
    await expect(v.getByTestId('parts-search-results').getByText(`AN-${k}`)).toBeVisible();
  }
});
