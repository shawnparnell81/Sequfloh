import { expect, test, type Browser } from '@playwright/test';
import { signIn, view } from './support';

/** Audit trail only on the Admin page and, for people given the audit permission, in their own Settings page. Made-up accounts only. */
test('no audit history on record pages; Settings audit section follows the permission', async ({ page, browser }) => {
  test.setTimeout(120_000);
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const stamp = Date.now().toString().slice(-6);
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const wi = (await ok(rq.post('/api/sequfloh/instructions', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, type: 'Validation', title: `Audit vis ${stamp}`, siteId: site } }))).id as string;
  const ir = (await ok(rq.post('/api/sequfloh/inspection-requests', { data: { number: `E2E-${Math.random().toString(36).slice(2, 10)}`, siteId: site, partNumber: `AV-${stamp}`, priority: 'Normal', requestedDate: '2026-11-20' } }))).id as string;
  const part = (await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `AV-${stamp}`, name: 'Test washer', partType: 0, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: 'Test', isRepairable: false, isSerialized: false, isLotControlled: false } } }))).id as string;
  const folder = (await ok(rq.post('/api/drawings/folders', { data: { name: `AuditVis ${stamp}`, siteId: site } }))).id as string;
  const drawing = (await ok(rq.post(`/api/drawings/folders/${folder}/drawings`, { multipart: {
    file: { name: 'test.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.4\n% made-up\n%%EOF\n') }, title: 'Test plate', drawingNumber: `DWG-AV${stamp}`, revision: 'A' } }))).id as string;
  for (const url of [`/sequfloh/instruction/${wi}`, `/sequfloh/inspection-request/${ir}`, `/sequfloh/parts/${part}`, `/sequfloh/drawings/d/${drawing}`, `/sequfloh/drawings?folder=${folder}`]) {
    await page.goto(url);
    await expect(view(page).locator('[data-record-actions]').first(), url).toBeVisible();
    await expect(view(page).getByRole('tab', { name: 'History' }), url).toHaveCount(0);
    await expect(view(page).getByRole('button', { name: 'History' }), url).toHaveCount(0);
  }
  // admin: full trail on the Admin page, filterable by record
  await page.goto(`/admin/audit-trail?entityId=${wi}`);
  await expect(view(page).getByTestId('audit-filters')).toBeVisible();

  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `Audit viewer ${stamp}`, description: 'made-up test role', permissions: ['sequfloh.access', 'sequfloh.view_audit_trail'] } }))) as string;
  const plainRole = (await ok(rq.post('/api/admin/roles', { data: { name: `Plain ${stamp}`, description: 'made-up test role', permissions: ['sequfloh.access'] } }))) as string;
  const mk = async (who: string, roleId: string) => {
    const email = `${who}.${stamp}@example.test`, password = `Pw-${stamp}-${Math.random().toString(36).slice(2)}`;
    const id = (await ok(rq.post('/api/admin/users', { data: { email, displayName: `Test ${who} ${stamp}`, password, mustChangePassword: false, defaultSiteId: site } }))) as string;
    await ok(rq.post(`/api/admin/users/${id}/roles`, { data: { roleId, siteId: site, departmentId: null } }));
    return { email, password };
  };
  const as = async (b: Browser, u: { email: string; password: string }) => {
    const ctx = await b.newContext(); const p = await ctx.newPage();
    await p.goto('/'); await p.fill('input[type=email]', u.email); await p.fill('input[type=password]', u.password); await p.click('button[type=submit]');
    await expect(p.getByTestId('tab-strip')).toBeVisible();
    return { ctx, p };
  };
  const plain = await as(browser, await mk('plain', plainRole));
  await plain.p.goto('/account/preferences');
  await expect(view(plain.p).getByRole('heading', { name: 'Settings' }).first()).toBeVisible();
  await expect(plain.p.getByTestId('settings-audit-trail')).toHaveCount(0);
  expect((await plain.p.request.get('/api/sequfloh/audit/system')).status()).toBe(403);
  expect((await plain.p.request.get(`/api/sequfloh/instructions/${wi}/history`)).status()).toBe(403);
  await plain.ctx.close();

  const viewer = await as(browser, await mk('auditor', role));
  await viewer.p.goto('/account/preferences');
  await expect(viewer.p.getByTestId('settings-audit-trail')).toBeVisible();
  expect((await viewer.p.request.get('/api/sequfloh/audit/system')).status()).toBe(200);
  expect((await viewer.p.request.get('/api/audit-trail')).status()).toBe(403);
  await viewer.ctx.close();
});
