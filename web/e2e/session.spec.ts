import { test, expect } from '@playwright/test';
import { section, settledShot, signIn, tabs } from './support';

const shots = process.env.E2E_SESSION_SHOTS;

test('closing the browser ends the session and signing in starts with Home only', async ({ page, browser }) => {
  await signIn(page);
  await section(page, 'Parts and Items');
  await section(page, 'Work Orders');
  await expect(tabs(page)).toHaveCount(3);
  if (shots) await settledShot(page, `${shots}/01-tabs-before-sign-out.png`);

  // the sign-in cookie is a session cookie, so the browser drops it when it closes
  const cookies = await page.context().cookies();
  const session = cookies.find((c) => c.name === 'sqf.session');
  expect(session?.expires).toBe(-1);
  expect(cookies.some((c) => c.expires > 0 && c.name.startsWith('sqf'))).toBe(false);
  // nothing about the open tabs is kept beyond this browser tab
  expect(await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('sqf.tabs.')))).toEqual([]);

  // a reopened browser keeps only lasting cookies and storage: none of them signs anyone in
  const reopened = await browser.newContext({ storageState: { cookies: cookies.filter((c) => c.expires > 0), origins: [] } });
  const again = await reopened.newPage();
  await again.goto('/');
  await expect(again.locator('input[type=password]')).toBeVisible();
  if (shots) await settledShot(again, `${shots}/02-reopened-browser-sign-in.png`);
  await reopened.close();

  // sign out, then sign in again in the same browser tab: the earlier tabs are not brought back
  await page.getByTestId('user-menu').click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.locator('input[type=password]')).toBeVisible();
  await signIn(page);
  await expect(tabs(page)).toHaveCount(1);
  if (shots) await settledShot(page, `${shots}/03-sign-in-home-only.png`);
});
