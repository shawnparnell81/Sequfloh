import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Shop day clock: banner, indirect task, break, timesheets, team, a correction, and the reference list. Made-up data only. */
test('day clock from sign-in through a correction', async ({ page }) => {
  const shots = process.env.E2E_SHOTS;
  let n = 0;
  const shot = async (name: string) => { n++; if (shots) await settledShot(page, path.join(shots, `${String(n).padStart(2, '0')}-${name}.png`)); };
  await signIn(page);
  const status = await page.request.get('/api/sequfloh/day-clock');
  if ((await status.json()).clockedIn) {
    await page.request.post('/api/sequfloh/day-clock/out');
    await page.reload();
    await expect(page.getByTestId('tab-strip')).toBeVisible();
  }
  await expect(page.getByTestId('day-clock-banner')).toBeVisible();
  await expect(page.getByTestId('day-clock-in')).toBeVisible();
  await shot('clock-in-banner');

  await page.getByTestId('day-clock-in').click();
  await expect(page.getByTestId('day-clock-widget')).toBeVisible();
  await expect(page.getByTestId('day-clock-task')).toBeVisible();
  await page.getByTestId('day-clock-task').click();
  await expect(page.getByRole('option', { name: 'Lab Setup' })).toBeVisible();
  await shot('clocked-in-indirect-picker');
  await page.keyboard.press('Escape');

  await page.getByTestId('day-clock-task').click();
  await page.getByRole('option', { name: 'Lab Setup' }).click();
  await page.getByTestId('day-clock-note').fill('Benches');
  await page.getByTestId('day-clock-start-task').click();
  await expect(page.getByTestId('day-clock-activity')).toContainText('Lab Setup');
  await page.getByTestId('day-clock-break').click();
  await expect(page.getByTestId('day-clock-activity')).toContainText('Break');
  await expect(page.getByTestId('day-clock-resume')).toBeVisible();
  await shot('break');

  await page.getByTestId('day-clock-resume').click();
  await expect(page.getByTestId('day-clock-activity')).toContainText('Lab Setup');

  await page.getByTestId('day-clock-timesheet-link').click();
  const daily = view(page);
  await expect(daily.getByTestId('timesheet-daily')).toBeVisible();
  await expect(daily.getByTestId('timesheet-daily')).toContainText('Lab Setup');
  await expect(daily.getByTestId('timesheet-daily')).toContainText('Arrived');
  await shot('daily-timesheet');

  await daily.locator('[data-testid="section-menu"]').getByRole('link', { name: 'This week' }).click();
  await expect(view(page).getByTestId('timesheet-weekly')).toBeVisible();
  await shot('weekly-timesheet');

  await view(page).locator('[data-testid="section-menu"]').getByRole('link', { name: 'Team' }).click();
  await expect(view(page).getByTestId('team-live')).toBeVisible();
  await expect(view(page).getByTestId('team-row').first()).toBeVisible();
  await shot('team-live');

  await view(page).getByTestId('correction-open').click();
  const dialog = page.getByTestId('correction-dialog');
  await expect(dialog).toBeVisible();
  await shot('correction-dialog');
  const start = new Date(Date.now() - (40 + Math.floor(Math.random() * 300)) * 24 * 3600 * 1000);
  const end = new Date(start.getTime() + 6 * 3600 * 1000);
  const local = (d: Date) => {
    const parts = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/New_York', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).formatToParts(d);
    const g = (t: string) => parts.find((p) => p.type === t)?.value ?? '';
    const hour = g('hour') === '24' ? '00' : g('hour');
    return `${g('year')}-${g('month')}-${g('day')}T${hour}:${g('minute')}`;
  };
  await dialog.getByLabel('Start (Eastern time)').fill(local(start));
  await dialog.getByLabel('End (Eastern time)').fill(local(end));
  await dialog.getByLabel('Reason').fill('Missed punch from yesterday');
  await dialog.getByTestId('correction-save').click();
  await expect(dialog).toBeHidden();
  await expect(view(page).getByTestId('day-clock-history')).toContainText('Missed punch from yesterday');

  await page.goto('/sequfloh/admin/reference');
  const ref = view(page);
  await ref.getByLabel('Search lists').fill('Indirect');
  await expect(ref.getByTestId('ref-list-indirect-tasks')).toBeVisible();
  await ref.getByTestId('ref-list-indirect-tasks').getByRole('link', { name: 'Indirect tasks' }).click();
  await expect(view(page).getByTestId('dropdown-table')).toContainText('Lab Setup');
  await expect(view(page).getByTestId('dropdown-table')).toContainText('Lab Cleaning');
  await page.mouse.move(8, 8);
  await shot('reference-indirect-tasks');
  await page.request.post('/api/sequfloh/day-clock/out');
});
