import { expect, test, type Browser } from '@playwright/test';
import { signIn, view } from './support';

const LINES = ['Air Suspension Development', 'BWS Development', 'Coil Spring Development', 'CSA Development', 'Fuel Injector Development', 'Fuel Pump Development',
  'Gas Lift Development', 'HD Shock Development', 'Shock Development', 'Legacy'];

/** Engineering, Development: one tab per product line, import into the line, listing per line, audit, and the import permission. Made-up data only. */
test('development files: product line tabs, import into a line, per-line listing and permissions', async ({ page, browser }) => {
  test.setTimeout(120_000);
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;

  await page.goto('/sequfloh/engineering/development');
  for (const l of LINES) await expect(view(page).getByRole('tab', { name: l, exact: true })).toBeVisible();
  await expect(view(page).getByRole('tab', { name: 'Legacy Development' })).toHaveCount(0);

  await view(page).getByRole('tab', { name: 'Fuel Pump Development', exact: true }).click();
  await expect(page).toHaveURL(/line=fuel-pump/);
  await view(page).getByTestId('development-files-files-input').setInputFiles({ name: `Pump trial ${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('Check,Result\nFlow,OK\n') });
  const dlg = page.getByTestId('bulk-import');
  await expect(dlg.getByTestId('development-import-line')).toContainText('Fuel Pump Development');
  await dlg.getByTestId('bulk-import-start').click();
  await expect(dlg.getByText('Imported', { exact: true })).toBeVisible();
  await dlg.getByRole('button', { name: 'Close' }).click();
  await expect(view(page).getByRole('row').filter({ hasText: `Pump trial ${stamp}` })).toBeVisible();

  // listed only under its own line, keeping its own number
  const pump = await ok(rq.get('/api/imported-files?development=fuel-pump&pageSize=200'));
  const row = pump.items.find((x: { title: string }) => x.title === `Pump trial ${stamp}`);
  expect(row.number).toBe(`Pump trial ${stamp}`);
  const legacy = await ok(rq.get('/api/imported-files?development=legacy&pageSize=200'));
  expect(legacy.items.some((x: { id: string }) => x.id === row.id)).toBeFalsy();
  await view(page).getByRole('tab', { name: 'Legacy', exact: true }).click();
  await expect(view(page).getByRole('row').filter({ hasText: `Pump trial ${stamp}` })).toHaveCount(0);

  // an unknown line is refused
  const bad = await rq.post('/api/imported-files', { multipart: { file: { name: `bad-${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('a\n1\n') }, developmentLine: 'nope' } });
  expect(bad.status()).toBe(400);

  // audit: who, what, when, description
  await page.goto(`/admin/audit-trail?entityId=${row.id}`);
  await expect(view(page).getByText(`into Fuel Pump Development`).first()).toBeVisible();

  // a viewer without the import permission sees the files but cannot import
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `Dev viewer ${stamp}`, description: 'made-up test role',
    permissions: ['sequfloh.access', 'sequfloh.view_engineering_folder', 'sequfloh.view_imported_files'] } }))) as string;
  const email = `devview.${stamp}@example.test`, password = `Pw-${stamp}-${Math.random().toString(36).slice(2)}`;
  const id = (await ok(rq.post('/api/admin/users', { data: { email, displayName: `Test dev viewer ${stamp}`, password, mustChangePassword: false, defaultSiteId: site } }))) as string;
  await ok(rq.post(`/api/admin/users/${id}/roles`, { data: { roleId: role, siteId: site, departmentId: null } }));
  const asViewer = async (b: Browser) => {
    const ctx = await b.newContext(); const p = await ctx.newPage();
    await p.goto('/'); await p.fill('input[type=email]', email); await p.fill('input[type=password]', password); await p.click('button[type=submit]');
    await expect(p.getByTestId('tab-strip')).toBeVisible();
    return { ctx, p };
  };
  const v = await asViewer(browser);
  await v.p.goto('/sequfloh/engineering/development?line=fuel-pump');
  await expect(view(v.p).getByRole('row').filter({ hasText: `Pump trial ${stamp}` })).toBeVisible();
  await expect(view(v.p).getByRole('button', { name: 'Import file' })).toHaveCount(0);
  const denied = await v.p.request.post('/api/imported-files', { multipart: { file: { name: `no-${stamp}.csv`, mimeType: 'text/csv', buffer: Buffer.from('a\n1\n') }, developmentLine: 'fuel-pump' } });
  expect(denied.status()).toBe(403);
  await v.ctx.close();
});
