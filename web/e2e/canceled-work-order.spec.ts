import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

/** Made-up records only. A canceled work order stays in the list with its own colour but can't be opened. */
test('canceled work order is listed but can not be opened', async ({ page }) => {
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const number = `E2E-${Math.random().toString(36).slice(2, 10)}`;
  const wo = (await ok(rq.post('/api/sequfloh/tracking', { data: { number, type: 'Production', siteId: site } }))).id as string;
  const ver = (await ok(rq.get(`/api/sequfloh/tracking/${wo}`))).record.stateVersion;
  await ok(rq.post(`/api/sequfloh/tracking/${wo}/actions/cancel`, { data: { operationId: crypto.randomUUID(), stateVersion: ver, reason: 'Made by mistake' } }));

  await page.goto(`/sequfloh/tracking/production?q=${number}`);
  const v = view(page);
  const row = v.locator('tr[data-canceled]', { hasText: number });
  await expect(row).toBeVisible();
  await row.click();
  await expect(page).toHaveURL(/\/sequfloh\/tracking\/production/);

  await page.goto(`/sequfloh/record/${wo}`);
  await expect(view(page).getByTestId('wo-canceled')).toHaveText("This work order is canceled and can't be opened.");
});
