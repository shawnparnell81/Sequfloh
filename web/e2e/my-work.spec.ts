import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

/** Made-up records and accounts only. Home opens on My Work: assigned work with due date and who assigned it, and private notes. */
test('my work: assigned, overdue first, notes private to their owner', async ({ page, browser }) => {
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const stamp = Date.now().toString().slice(-6);
  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `My work ${stamp}`, description: 'made-up test role', permissions: ['sequfloh.access', 'sequfloh.view_dashboard'] } }))) as string;
  const mk = async (tag: string) => {
    const email = `${tag}.${stamp}@example.test`, password = `Pw-${stamp}-${Math.random().toString(36).slice(2)}`;
    const id = (await ok(rq.post('/api/admin/users', { data: { email, displayName: `Test ${tag} ${stamp}`, password, mustChangePassword: false, defaultSiteId: site } }))) as string;
    await ok(rq.post(`/api/admin/users/${id}/roles`, { data: { roleId: role, siteId: site, departmentId: null } }));
    return { id, email, password };
  };
  const a = await mk('mwa'), b = await mk('mwb');
  const wo = async (n: string, days: number) => {
    const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { number: `E2E-${n}-${stamp}`, type: 'Inspection', siteId: site, inspectionSubType: 'AQL', inspectionDescription: `Made-up check ${n}`,
      workOrder: `WO-${n}-${stamp}`, dueAt: new Date(Date.now() + days * 86_400_000).toISOString(), minBenchmarkMinutes: 0, maxBenchmarkMinutes: 600 } }))).id as string;
    const v = (await ok(rq.get(`/api/sequfloh/tracking/${id}`))).record.stateVersion;
    await ok(rq.post(`/api/sequfloh/tracking/${id}/actions/dispatch`, { data: { operationId: crypto.randomUUID(), stateVersion: v, assignedUserId: a.id } }));
  };
  await wo('LATER', 4); await wo('LATE', -2);
  const as = async (u: { email: string; password: string }) => {
    const ctx = await browser.newContext(); const p = await ctx.newPage();
    await p.goto('/'); await p.fill('input[type=email]', u.email); await p.fill('input[type=password]', u.password); await p.click('button[type=submit]');
    await expect(view(p).getByTestId('my-work-page')).toBeVisible();
    return { ctx, p };
  };
  const ua = await as(a);
  const items = view(ua.p).getByTestId('my-work-item');
  await expect(items).toHaveCount(2);
  await expect(items.first()).toContainText(`WO-LATE-${stamp}`);
  await expect(items.first()).toContainText('Overdue');
  await expect(items.first()).toContainText('Made-up check LATE');
  await expect(items.first()).toContainText('Assigned by');
  const box = view(ua.p).getByRole('textbox', { name: 'Add a note' });
  await box.fill('Made-up reminder to call the lab'); await box.press('Enter');
  const note = view(ua.p).getByTestId('note');
  await expect(note).toContainText('Made-up reminder to call the lab');
  await note.getByRole('checkbox').click();
  await expect(view(ua.p).getByRole('button', { name: 'Completed (1)' })).toBeVisible();
  await ua.ctx.close();

  const ub = await as(b);
  await expect(view(ub.p).getByTestId('my-work-empty')).toBeVisible();
  await expect(view(ub.p).getByTestId('notes-empty')).toBeVisible();
  await ub.ctx.close();
});
