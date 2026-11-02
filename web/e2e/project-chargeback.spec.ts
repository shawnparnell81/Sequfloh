import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Made-up records only. A project chargeback links every work order in the project, totals the man hours and shows a per work order breakdown. */
test('project chargeback links all work orders and totals the hours', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const tag = Math.random().toString(36).slice(2, 8).toUpperCase();
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  const project = (await ok(rq.post('/api/sequfloh/projects', { data: { number: `PRJ-${tag}`, name: 'Made-up rework project', siteId: site } }))).id as string;
  const start = Date.parse('2026-10-05T13:00:00Z');
  const at = (h: number) => new Date(start + h * 3600_000).toISOString();
  const wos: [string, string, number, number][] = [[`PA-${tag}`, 'Made-up Ann', 0, 3], [`PB-${tag}`, 'Made-up Ben', 48, 2.5], [`PC-${tag}`, 'Made-up Cal', 72, 4]];
  for (const [number, name, offset, hours] of wos) {
    const id = (await ok(rq.post('/api/sequfloh/tracking', { data: { type: 'Inspection', siteId: site, number, projectId: project } }))).id as string;
    const worker = (await ok(rq.post(`/api/sequfloh/tracking/${id}/crew/workers`, { data: { name: `${name} ${tag.slice(0, 3)}` } }))).workers[0].id as string;
    await ok(rq.post(`/api/sequfloh/tracking/${id}/crew/sessions`, { data: { crewSize: 1, workerId: worker, start: at(offset), stop: at(offset + hours + 0.5),
      pauses: [{ from: at(offset + 1), to: at(offset + 1.5) }] } }));
  }

  await page.goto(`/sequfloh/project/${project}`);
  const action = view(page).getByTestId('project-chargeback');
  await expect(action).toBeVisible();
  if (shots) await settledShot(page, path.join(shots, '01-project-page-chargeback-action.png'));
  await action.click();
  const list = view(page).getByTestId('cb-linked-list');
  await expect(list).toBeVisible();
  for (const [number] of wos) await expect(view(page).getByTestId(`cb-linked-${number}`)).toBeChecked();
  const totals = view(page).getByTestId('cb-linked-totals');
  await expect(totals).toContainText('9.50');
  await view(page).getByTestId(`cb-linked-PC-${tag}`).uncheck();
  await expect(totals).toContainText('5.50');
  await expect(view(page).getByTestId('cb-total-hours')).toHaveValue('5.5');
  await view(page).getByLabel('Hourly rate (optional)').fill('40');
  await list.scrollIntoViewIfNeeded();
  if (shots) await page.screenshot({ path: path.join(shots, '02-work-order-tick-list.png'), fullPage: true, animations: 'disabled' });
  if (shots) await list.locator('xpath=..').locator('xpath=..').screenshot({ path: path.join(shots, '02b-tick-list-close-up.png'), animations: 'disabled' });
  await view(page).getByTestId('save-chargeback').click();

  const v = view(page).getByTestId('chargeback-view');
  const breakdown = v.locator('[data-section="linkedWorkOrders"]');
  await expect(breakdown).toContainText(`PA-${tag}`);
  await expect(breakdown).toContainText(`PB-${tag}`);
  await expect(breakdown).not.toContainText(`PC-${tag}`);
  await expect(breakdown).toContainText('5.50');
  await expect(v).toContainText('220');
  const id = page.url().split('/chargebacks/')[1].split('/')[0];
  expect((await rq.get(`/api/sequfloh/chargebacks/${id}/pdf`)).ok()).toBeTruthy();
  expect((await rq.get(`/api/sequfloh/chargebacks/${id}/xlsx`)).ok()).toBeTruthy();
  if (shots) await page.screenshot({ path: path.join(shots, '03-report-breakdown-and-totals.png'), fullPage: true, animations: 'disabled' });
  if (shots) await breakdown.screenshot({ path: path.join(shots, '03b-breakdown-close-up.png'), animations: 'disabled' });
});
