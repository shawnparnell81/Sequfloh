import { expect, type Page } from '@playwright/test';

/** A local account with administrator rights; set E2E_EMAIL and E2E_PASSWORD for the local database. */
export const account = () => {
  const email = process.env.E2E_EMAIL, password = process.env.E2E_PASSWORD;
  if (!email || !password) throw new Error('Set E2E_EMAIL and E2E_PASSWORD for a local account.');
  return { email, password };
};
export async function signIn(page: Page) {
  const { email, password } = account();
  await page.goto('/');
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await expect(page.getByTestId('tab-strip')).toBeVisible();
}
export const view = (p: Page) => p.locator('[data-testid^="tab-view-"][data-active="true"]');
export const strip = (p: Page) => p.getByTestId('tab-strip');
export const tabs = (p: Page) => strip(p).locator('[data-tab-url]');
export const section = async (p: Page, label: string) => { await p.locator(`[data-testid="section-${label}"]:visible`).first().click(); await p.waitForTimeout(500); };
