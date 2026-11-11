import fs from 'node:fs';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { settledShot, signIn, view } from './support';

fs.mkdirSync('/tmp/sequfloh-shots', { recursive: true });

/** An administrator picks a manager on the person. That manager sees the report's timesheet and My team. Made-up accounts only. */
test('a manager chosen on the person can open that person\'s day clock', async ({ page, browser }) => {
  test.setTimeout(120_000);
  const stamp = Date.now().toString().slice(-6);
  const managerName = `Dana Lead ${stamp}`;
  const workerName = `Riley Report ${stamp}`;
  const password = `Pw-${stamp}-clock`;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => {
    const x = await r;
    expect(x.ok(), await x.text()).toBeTruthy();
    return x.status() === 204 ? null : x.json();
  };

  await signIn(page);
  const rq = page.request;
  const role = (await ok(rq.post('/api/admin/roles', { data: { name: `Day clock access ${stamp}`, description: 'Sign in and use the day clock', permissions: ['sequfloh.access'] } }))) as string;
  const mk = async (name: string, who: string) => {
    const email = `${who}.${stamp}@example.test`;
    const id = (await ok(rq.post('/api/admin/users', { data: { email, displayName: name, password, mustChangePassword: false } }))) as string;
    await ok(rq.post(`/api/admin/users/${id}/roles`, { data: { roleId: role, siteId: null, departmentId: null } }));
    return { id, email };
  };
  const manager = await mk(managerName, 'dana');
  const worker = await mk(workerName, 'riley');

  await page.goto('/admin/users');
  await view(page).getByRole('row', { name: workerName }).getByRole('button', { name: 'Edit' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('combobox', { name: 'Manager' }).click();
  await page.getByRole('option', { name: managerName, exact: true }).click();
  await expect(dialog.getByRole('combobox', { name: 'Manager' })).toHaveText(managerName);
  await settledShot(page, '/tmp/sequfloh-shots/manager-user-edit.png');
  await dialog.getByRole('button', { name: 'Save details' }).click();
  await expect(dialog.getByText('Saved.')).toBeVisible();

  const as = async (b: Browser, email: string) => {
    const ctx = await b.newContext();
    const p = await ctx.newPage();
    await p.goto('/');
    await p.fill('input[type=email]', email);
    await p.fill('input[type=password]', password);
    await p.click('button[type=submit]');
    await expect(p.getByTestId('tab-strip')).toBeVisible();
    return { ctx, p };
  };
  const report = await as(browser, worker.email);
  expect((await report.p.request.post('/api/sequfloh/day-clock/in')).ok()).toBeTruthy();
  await report.ctx.close();

  const lead = await as(browser, manager.email);
  await openSheet(lead.p, workerName);
  await settledShot(lead.p, '/tmp/sequfloh-shots/manager-report-timesheet.png');
  await lead.p.goto('/sequfloh/day-clock/team');
  await expect(view(lead.p).getByTestId('my-team')).toBeVisible();
  await expect(view(lead.p).getByTestId('my-team-row').filter({ hasText: workerName })).toBeVisible();
  await settledShot(lead.p, '/tmp/sequfloh-shots/manager-team-view.png');
  await lead.ctx.close();

  expect(manager.id).toBeTruthy();
  expect(worker.id).toBeTruthy();
});

async function openSheet(p: Page, workerName: string) {
  await p.goto('/sequfloh/day-clock');
  const box = view(p);
  await expect(box.getByTestId('timesheet-person')).toBeVisible();
  await box.getByTestId('timesheet-person').click();
  await p.getByRole('option', { name: workerName, exact: true }).click();
  await expect(box.getByTestId('timesheet-who')).toContainText(workerName);
  await expect(box.getByTestId('timesheet-daily')).toBeVisible();
}
