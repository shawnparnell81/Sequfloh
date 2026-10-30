import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Product family setup: the wide list stays inside the window, rows can be dragged past the visible edge, and "What uses it" names what blocks a delete. Made-up data only. */
for (const size of [{ width: 1024, height: 600 }, { width: 1280, height: 800 }]) test(`product family setup fits the window, scrolls while dragging and lists what uses a family at ${size.width}x${size.height}`, async ({ page }) => {
  const shots = process.env.E2E_PF_SHOTS;
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${size.width}x${size.height}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const k = Date.now().toString(36).toUpperCase();
  await page.setViewportSize(size);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const fam = (await ok(rq.post('/api/sequfloh/reference/product-families', { data: { code: `PF${k}`, label: `Example family ${k}`, description: null } }))).id as string;
  for (const n of [1, 2]) await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `PFS-${k}-${n}`, name: 'Example part', description: '', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: null, specification: null }, typeId: fam, attributes: null } }));

  await page.goto('/sequfloh/admin/dropdowns?list=product-families');
  const table = view(page).getByTestId('dropdown-table');
  await expect(table.getByTestId('dropdown-row').first()).toBeVisible();
  // the list ends inside the window, so its side-to-side scrollbar is on screen without scrolling the page to the bottom
  const box = await table.evaluate((el) => ({ height: el.getBoundingClientRect().height, inner: window.innerHeight, sw: el.scrollWidth, cw: el.clientWidth, sh: el.scrollHeight, ch: el.clientHeight }));
  // never squeezed to a few rows: at least most of the window (or all rows), never taller than the window
  expect(box.height).toBeGreaterThanOrEqual(Math.min(480, box.inner - 64, box.sh) - 2);
  expect(box.height).toBeLessThanOrEqual(box.inner);
  await table.evaluate((el) => el.scrollIntoView({ block: 'end' }));
  const bottom = await table.evaluate((el) => el.getBoundingClientRect().bottom);
  expect(bottom).toBeLessThanOrEqual(box.inner + 1);
  await table.evaluate((el) => { el.scrollLeft = el.scrollWidth; });
  await expect(view(page).getByTestId('reference-delete').first()).toBeInViewport();
  await table.evaluate((el) => { el.scrollLeft = 0; });
  await shot('01-fits-window');

  // holding a dragged row at the bottom edge scrolls the list
  const first = table.getByTestId('dropdown-row').first();
  const handle = first.locator('svg[aria-label^="Drag "]');
  const hb = (await handle.boundingBox())!;
  const tb = (await table.boundingBox())!;
  await page.mouse.move(hb.x + hb.width / 2, hb.y + hb.height / 2);
  await page.mouse.down();
  await page.mouse.move(hb.x + 10, hb.y + 20, { steps: 4 });
  for (let i = 0; i < 20; i++) { await page.mouse.move(hb.x + 10, tb.y + tb.height - 8 - (i % 2), { steps: 1 }); await page.waitForTimeout(50); }
  const scrolled = await table.evaluate((el) => el.scrollTop);
  await page.mouse.up();
  expect(scrolled).toBeGreaterThan(0);

  // What uses it: the same part number count as the Part Master card
  await page.goto('/sequfloh/admin/dropdowns?list=product-families');
  const row = view(page).getByTestId('dropdown-row').filter({ hasText: `Example family ${k}` });
  await row.scrollIntoViewIfNeeded();
  await row.getByTestId('reference-uses').click();
  await expect(page.getByTestId('uses-parts')).toHaveText('Parts and Items: 2 part numbers');
  await expect(page.getByTestId('uses-files')).toHaveText('Imported files: 0');
  await shot('02-what-uses-it');
  await page.getByRole('button', { name: 'Close' }).click();
  await row.getByTestId('reference-delete').click();
  await page.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('dialog').getByText(`"Example family ${k}" is used by 2 part numbers in Parts and Items.`, { exact: false })).toBeVisible();
  await shot('03-delete-blocked');
  await page.getByRole('button', { name: 'Cancel' }).click();

  // move what uses it to another family, then delete
  const target = (await ok(rq.post('/api/sequfloh/reference/product-families', { data: { code: `PT${k}`, label: `Example target ${k}`, description: null } }))).id as string;
  await page.reload();
  const row2 = view(page).getByTestId('dropdown-row').filter({ hasText: `Example family ${k}` });
  await row2.scrollIntoViewIfNeeded();
  await row2.getByTestId('reference-uses').click();
  await page.getByTestId('uses-dialog').getByLabel('Move them to').click();
  await page.getByRole('option', { name: `Example target ${k}` }).click();
  await expect(page.getByTestId('uses-move-note')).toContainText(`to Example target ${k}`);
  await shot('04-move-and-delete');
  await page.getByTestId('uses-move-delete').click();
  await expect(page.getByTestId('uses-dialog')).toBeHidden();
  await expect(view(page).getByTestId('dropdown-row').filter({ hasText: `Example family ${k}` })).toHaveCount(0);
  const counts = await ok(rq.get('/api/sequfloh/parts/family-counts')) as { typeId: string | null; count: number }[];
  expect(counts.find((c) => c.typeId === target)?.count).toBe(2);
});
