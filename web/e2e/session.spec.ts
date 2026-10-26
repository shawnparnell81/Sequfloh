import { test, expect } from '@playwright/test';
import { account, section, settledShot, signIn, tabs } from './support';

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

test('a browser that brings the session back still shows sign-in, and Remember me offers Sign in as', async ({ page, browser }) => {
  const { email, password } = account();
  await page.goto('/');
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  await page.getByLabel(/Remember me/).check();
  await page.click('button[type=submit]');
  await expect(page.getByTestId('tab-strip')).toBeVisible();
  // a reload and a new tab in the same browser stay signed in
  await page.reload();
  await expect(page.getByTestId('tab-strip')).toBeVisible();
  const second = await page.context().newPage();
  await second.goto('/');
  await expect(second.getByTestId('tab-strip')).toBeVisible();
  await second.close();

  // the remember cookie is lasting; the session cookie is not
  const cookies = await page.context().cookies();
  expect(cookies.find((c) => c.name === 'sqf.session')?.expires).toBe(-1);
  expect(cookies.find((c) => c.name === 'sqf.remember')!.expires).toBeGreaterThan(0);

  // "Continue where you left off": every cookie comes back, but the browser was closed
  const restored = await browser.newContext({ storageState: { cookies, origins: [] } });
  const again = await restored.newPage();
  await again.goto('/');
  await expect(again.getByTestId('sign-in-remembered')).toBeVisible();
  await again.getByTestId('sign-in-remembered').click();
  await expect(again.getByTestId('tab-strip')).toBeVisible();

  // signing out keeps Remember me; "Use a different account" forgets it
  await again.getByTestId('user-menu').click();
  await again.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(again.getByTestId('sign-in-remembered')).toBeVisible();
  await again.getByTestId('use-different-account').click();
  await expect(again.locator('input[type=password]')).toBeVisible();
  await restored.close();
});
