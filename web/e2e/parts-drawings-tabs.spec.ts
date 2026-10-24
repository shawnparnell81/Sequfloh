import path from 'node:path';
import { expect, test } from '@playwright/test';
import { section, settledShot, signIn, view } from './support';

/** Made-up families and drawings only. */
test('Parts Drawings: sidebar entry, family tabs, scoped search and old links redirect', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.json(); };
  const site = (await (await rq.get('/api/organization/sites')).json())[0].id;
  const pdf = { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up test drawing\n%%EOF\n') };
  const pumps = (await ok(rq.post('/api/drawings/folders', { data: { name: `Zeta Pump ${stamp} Drawings`, siteId: site } }))).id as string;
  const valves = (await ok(rq.post('/api/drawings/folders', { data: { name: `Zeta Valve ${stamp} Drawings`, siteId: site } }))).id as string;
  const sub = (await ok(rq.post('/api/drawings/folders', { data: { name: `PX-${stamp}`, parentId: pumps, siteId: site } }))).id as string;
  const pumpDwg = (await ok(rq.post(`/api/drawings/folders/${sub}/drawings`, { multipart: { file: pdf, title: `Impeller ${stamp}`, drawingNumber: `ZP-${stamp}`, revision: 'A' } }))).id as string;
  await ok(rq.post(`/api/drawings/folders/${valves}/drawings`, { multipart: { file: pdf, title: `Impeller seat ${stamp}`, drawingNumber: `ZV-${stamp}`, revision: 'A' } }));

  // top-level sidebar entry, not under Engineering
  await section(page, 'Parts Drawings');
  await expect(page).toHaveURL(/\/sequfloh\/drawings$/);
  await expect(view(page).getByRole('heading', { name: 'Parts Drawings' }).first()).toBeVisible();
  const tabs = view(page).getByTestId('family-tabs').getByRole('tab');
  await expect(tabs.first()).toHaveText('All');
  await expect(tabs.first()).toHaveAttribute('aria-selected', 'true');
  const pumpTab = view(page).getByRole('tab', { name: `Zeta Pump ${stamp}`, exact: true });
  await expect(pumpTab).toBeVisible();
  await expect(view(page).getByRole('tab', { name: `Zeta Valve ${stamp}`, exact: true })).toHaveCount(1);
  await expect(view(page).getByRole('tab', { name: /Drawings$/ })).toHaveCount(0);
  await shot('all-tab');

  // a family tab opens that family; its subfolders keep the tab selected
  await pumpTab.click();
  await expect(page).toHaveURL(new RegExp(`folder=${pumps}`));
  await expect(pumpTab).toHaveAttribute('aria-selected', 'true');
  await view(page).getByRole('button', { name: `PX-${stamp}` }).click();
  await expect(pumpTab).toHaveAttribute('aria-selected', 'true');
  await shot('family-tab');

  // search is scoped to the tab, with an option for all families
  await view(page).getByLabel(`Search Zeta Pump ${stamp} drawings`).fill(stamp);
  await view(page).getByRole('button', { name: 'Search', exact: true }).click();
  await expect(view(page).getByText(`ZP-${stamp}`)).toBeVisible();
  await expect(view(page).getByText(`ZV-${stamp}`)).toHaveCount(0);
  await shot('search-in-family');
  await view(page).getByTestId('search-all-families').click();
  await expect(page).toHaveURL(/all=1/);
  await expect(view(page).getByText(`ZV-${stamp}`)).toBeVisible();
  await expect(view(page).getByText(`ZP-${stamp}`)).toBeVisible();
  await shot('search-all-families');
  await view(page).getByTestId('family-tab-all').click();
  await expect(view(page).getByText(`ZV-${stamp}`)).toBeVisible();

  // the tab strip scrolls rather than wrapping
  await expect(view(page).getByTestId('family-tabs')).toHaveClass(/MuiTabs-scrollable|MuiTabs-root/);
  await expect(view(page).getByTestId('family-tabs').locator('.MuiTabs-scrollableX').first()).toBeAttached();

  // old Engineering links land on the same place with their query
  await page.goto(`/sequfloh/engineering/drawings?folder=${sub}`);
  await expect(page).toHaveURL(new RegExp(`/sequfloh/drawings\\?folder=${sub}$`));
  await expect(view(page).getByText(`ZP-${stamp}`)).toBeVisible();
  await page.goto(`/sequfloh/engineering/drawings/d/${pumpDwg}`);
  await expect(page).toHaveURL(new RegExp(`/sequfloh/drawings/d/${pumpDwg}$`));
  await expect(view(page).getByRole('heading', { name: new RegExp(`ZP-${stamp}`) }).first()).toBeVisible();
  await shot('old-link-redirected');
});
