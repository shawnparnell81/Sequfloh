import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. The key work order details sit at the top of the page, above the tabs, for every work order type and on a phone-sized screen. */
test('work order header shows the key details without scrolling', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const before = process.env.E2E_SHOTS_PREFIX === 'before';
  const shot = async (name: string) => { if (shots) await settledShot(page, path.join(shots, `${process.env.E2E_SHOTS_PREFIX ?? 'after'}-${name}.png`)); };
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const k = Math.random().toString(36).slice(2, 8).toUpperCase();
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const fam = (await ok(rq.get('/api/sequfloh/reference/product-families')) as { id: string; label: string }[])[0];
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `HDR-${k}`, name: 'Example housing', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { isRepairable: false, isSerialized: false, isLotControlled: false }, typeId: fam.id } }))).id as string;
  const due = new Date(Date.now() + 5 * 86400_000).toISOString();
  const orders: [string, Record<string, unknown>][] = [
    ['inspection', { type: 'Inspection', inspectionSubType: 'AQL', inspectionDescription: 'Made-up incoming lot check', partId: part, quantity: 25, priority: 'High', dueAt: due, lotNumber: `LOT-${k}` }],
    ['research', { type: 'Research', familyId: fam.id, inspectionDescription: 'Made-up research question', dueAt: due }],
    ['weight', { type: 'Weight', familyId: fam.id }],
  ];
  for (const [name, data] of orders) {
    const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-HDR-${name.slice(0, 3).toUpperCase()}-${k}`, siteId: site, ...data } }))).id as string;
    for (const [vp, size] of [['wide', { width: 1440, height: 900 }], ['narrow', { width: 390, height: 844 }]] as const) {
      await page.setViewportSize(size);
      await page.goto(`/sequfloh/record/${id}`);
      await expect(view(page).getByRole('tab', { name: 'Record' })).toBeVisible();
      if (!before) {
        const card = page.getByTestId('wo-summary');
        await expect(card).toBeVisible();
        await expect(card).toContainText('Status');
        await expect(card).toContainText('Created');
        await expect(card.locator('[data-summary-item="Qty completed"]')).toContainText(name === 'inspection' ? '0 of 25' : '0');
        if (name === 'inspection') { await expect(card).toContainText(`HDR-${k}`); await expect(card).toContainText('25'); await expect(card).toContainText('High'); }
        if (name !== 'inspection') await expect(card).toContainText(fam.label);
        if (vp === 'wide') { const b = await card.boundingBox(); expect(b && b.y + b.height).toBeLessThan(size.height); }
        await expect(view(page).getByRole('tab', { name: 'Steps' }).or(view(page).getByRole('tab', { name: 'Job' })).first()).toBeVisible();
      }
      await page.waitForTimeout(600);
      await shot(`${name}-${vp}`);
    }
  }
});
