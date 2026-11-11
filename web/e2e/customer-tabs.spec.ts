import { expect, test, type APIRequestContext } from '@playwright/test';
import { settledShot, signIn, view } from './support';

const shots = '/opt/cursor/artifacts';

/** Seeds two customers that share a drawing, then walks the customer tabs, the cross-reference, and search. */
test('customer tabs, also sold as, and search open the right customer', async ({ page }) => {
  const shot = (name: string) => settledShot(page, `${shots}/${name}.png`);
  const k = Date.now().toString(36).toUpperCase();
  const drawing = `9214-${k}`;
  const fl = `FL${k}`;
  const fr = `FR${k}`;
  const napa = `NU${k}`;
  const loose = `UA${k}`;
  await signIn(page);
  const rq = page.request;
  const site = (await (await rq.get('/api/organization/sites')).json())[0].id as string;
  const customers = await (await rq.get('/api/sequfloh/reference/customers')).json() as { id: string; label: string }[];
  const lines = await (await rq.get('/api/sequfloh/reference/product-families')).json() as { id: string; label: string }[];
  const oreilly = customers.find((c) => c.label === "O'Reilly")!.id;
  const napaUs = customers.find((c) => c.label === 'NAPA US')!.id;
  const csa = lines.find((l) => l.label === 'Complete Strut Assembly (CSA)')!.id;

  await importSheet(rq, site, oreilly, csa, `Part number,Description,Drawing number\n${fl},Example left strut,${drawing}\n${fr},Example right strut,${drawing}\n`);
  await importSheet(rq, site, napaUs, csa, `Part number,Description,Drawing number\n${napa},Example napa strut,${drawing}\n`);
  await rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: loose, name: 'Example unassigned', description: 'No customer yet', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: `9000-${k}` },
    typeId: csa,
  } });

  await page.goto('/sequfloh/parts');
  const v = view(page);
  await expect(v.getByTestId("customer-card-O'Reilly")).toBeVisible();
  await expect(v.getByTestId('customer-card-Unassigned')).toBeVisible();
  await expect(v.getByTestId('customer-card-SenSen')).toHaveCount(0);
  await shot('screenshot_customer_tabs_home');

  await v.getByTestId("customer-card-O'Reilly").locator('a').click();
  await expect(v.getByTestId('line-tab-Complete Strut Assembly (CSA)')).toBeVisible();
  await v.getByTestId('line-tab-Complete Strut Assembly (CSA)').click();
  const lineChip = v.getByTestId('line-tab-Complete Strut Assembly (CSA)');
  const customerChip = v.getByTestId(`customer-chip-${oreilly}`);
  await expect.poll(async () => lineChip.evaluate((el) => getComputedStyle(el).color)).toBe('rgb(255, 255, 255)');
  await expect.poll(async () => customerChip.evaluate((el) => getComputedStyle(el).color)).toBe('rgb(255, 255, 255)');
  await shot('screenshot_selected_product_line');
  await v.getByLabel('Search').first().fill(fl); await v.getByLabel('Search').first().press('Enter');
  const row = v.locator('tr').filter({ has: page.locator('span').filter({ hasText: new RegExp(`^${fl}$`) }) });
  await expect(row.getByTestId('also-sold-as')).toContainText(fr);
  await expect(row.getByTestId('also-sold-as')).toContainText(napa);
  await shot('screenshot_customer_line_also_sold_as');

  await row.getByTestId('also-sold-as-link').filter({ hasText: napa }).click();
  await expect(v.getByTestId('part-drawings-box')).toBeVisible();
  await shot('screenshot_part_detail_drawings');
  await expect(v.getByTestId('cross-references')).toContainText(fl);
  await expect(v.getByTestId('cross-references')).toContainText(fr);
  await v.getByTestId('cross-references').scrollIntoViewIfNeeded();
  await shot('screenshot_part_detail_cross_references');

  await page.goto('/sequfloh/parts');
  await v.getByTestId('parts-search').fill(napa);
  await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.getByTestId('search-landed')).toContainText('NAPA US');
  await expect(v.getByTestId('search-landed')).toContainText('Complete Strut Assembly (CSA)');
  await expect(v.getByTestId('found-part')).toContainText(napa);
  await shot('screenshot_search_lands_on_customer_line');

  await page.goto('/sequfloh/parts');
  await v.getByTestId('parts-search').fill(drawing);
  await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.getByTestId('parts-search-results')).toContainText(fl);
  await expect(v.getByTestId('parts-search-results')).toContainText(napa);
  await shot('screenshot_search_ambiguous_matches');

  await v.getByTestId('customer-card-Unassigned').locator('a').click();
  const listSearch = v.getByRole('textbox', { name: 'Search', exact: true });
  await listSearch.fill(loose);
  await v.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(v.locator('span').filter({ hasText: new RegExp(`^${loose}$`) })).toBeVisible();
  await shot('screenshot_unassigned_parts');
});

async function importSheet(rq: APIRequestContext, site: string, customerId: string, lineId: string, csv: string) {
  const read = await rq.post('/api/sequfloh/parts/import/read', {
    multipart: { file: { name: 'example.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) } },
  });
  expect(read.ok(), await read.text()).toBeTruthy();
  const t = await read.json() as { headers: string[]; rows: string[][]; mapping: Record<string, number> };
  const res = await rq.post('/api/sequfloh/parts/import', { data: {
    fileName: 'example.csv', headers: t.headers, rows: t.rows, mapping: t.mapping, siteId: site,
    customerId, defaultTypeId: lineId, updateExisting: false, fixed: { name: 'Complete Strut Assembly (CSA)' },
  } });
  expect(res.ok(), await res.text()).toBeTruthy();
}
