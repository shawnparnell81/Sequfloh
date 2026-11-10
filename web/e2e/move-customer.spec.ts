import { expect, test, type APIRequestContext } from '@playwright/test';
import { settledShot, signIn, view } from './support';

const shots = '/opt/cursor/artifacts';

/** Move imported parts to another customer: one part, then a whole product line, with a conflict left in place. */
test('move parts to a customer from the part page and from the list', async ({ page }) => {
  const shot = (name: string) => settledShot(page, `${shots}/${name}.png`);
  const k = Date.now().toString(36).toUpperCase();
  const dup = `MV${k}-01`;
  await signIn(page);
  const rq = page.request;
  const site = (await (await rq.get('/api/organization/sites')).json())[0].id as string;
  const customers = await (await rq.get('/api/sequfloh/reference/customers')).json() as { id: string; label: string }[];
  const lines = await (await rq.get('/api/sequfloh/reference/product-families')).json() as { id: string; label: string }[];
  const oreilly = customers.find((c) => c.label === "O'Reilly")!.id;
  const napa = customers.find((c) => c.label === 'NAPA US')!.id;
  const csa = lines.find((l) => l.label === 'Complete Strut Assembly (CSA)')!.id;
  const rows = [dup, ...Array.from({ length: 29 }, (_, i) => `MV${k}-${String(i + 2).padStart(2, '0')}`)];
  await importSheet(rq, site, oreilly, csa, `Part number,Description,Drawing number\n${dup},Example already there,D-${k}\n`);
  await importSheet(rq, site, napa, csa, ['Part number,Description,Drawing number', ...rows.map((pn) => `${pn},Example strut,D-${k}`)].join('\n'));

  const v = view(page);
  await page.goto(`/sequfloh/parts?customer=${napa}&partType=${csa}&q=MV${k}-02`);
  await v.getByText(`MV${k}-02`).click();
  await v.getByRole('button', { name: 'Edit' }).click();
  const edit = page.getByRole('dialog');
  await expect(edit.getByTestId('part-customer-select')).toBeVisible();
  await shot('screenshot_part_customer_field');
  await edit.getByRole('button', { name: 'Cancel' }).click();

  await page.goto(`/sequfloh/parts?customer=${napa}&partType=${csa}&q=MV${k}`);
  await v.getByRole('checkbox', { name: 'Select row' }).first().click();
  await expect(v.getByTestId('select-all-matching')).toContainText('selected on this page');
  await shot('screenshot_parts_selected');
  await v.getByRole('button', { name: /Select all \d+ matching/ }).click();
  await expect(v.getByTestId('select-all-matching')).toContainText('All 30 parts matching this list');
  await shot('screenshot_select_all_matching');

  await v.getByTestId('select-all-matching').getByRole('button', { name: 'Move to customer' }).click();
  const dlg = page.getByRole('dialog');
  await expect(dlg.getByRole('heading', { name: 'Move to customer' })).toBeVisible();
  await shot('screenshot_move_to_customer_dialog');
  await dlg.getByRole('combobox', { name: 'Customer' }).click();
  await page.getByRole('option', { name: "O'Reilly", exact: true }).click();
  await dlg.getByTestId('move-preview-button').click();
  await expect(dlg.getByTestId('move-preview')).toContainText('29 parts will move to O\'Reilly');
  await expect(dlg.getByTestId('move-conflicts')).toContainText(dup);
  await shot('screenshot_move_confirm_conflict');
  await dlg.getByTestId('move-confirm').click();
  await expect(dlg.getByTestId('move-result')).toContainText('Moved 29 parts');
  await expect(dlg.getByTestId('move-result')).toContainText('1 skipped');
  await shot('screenshot_move_result');
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
