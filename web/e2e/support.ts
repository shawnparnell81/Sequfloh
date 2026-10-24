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

/** Full-page-size screenshot taken only once dialog/backdrop transitions have finished, with any remaining animation frozen at its end state. */
export async function settledShot(page: Page, file: string) {
  await page.waitForFunction(() => document.getAnimations().every((a) => a.playState !== 'running'), undefined, { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => [...document.querySelectorAll('.MuiDialog-root .MuiBackdrop-root')].every((b) => { const o = getComputedStyle(b).opacity; return o === '1' || o === '0'; }), undefined, { timeout: 5000 }).catch(() => {});
  await page.screenshot({ path: file, animations: 'disabled', caret: 'hide' });
}
/** Chooses a part type in the open part dialog (the first active type unless one is named). */
export async function choosePartType(page: Page, name?: string) {
  await page.getByRole('dialog').getByRole('combobox', { name: /^Product family/ }).click();
  await (name ? page.getByRole('option', { name, exact: true }) : page.getByRole('option').first()).click();
}

/** Picks the part Name, which offers the product families list. */
export async function choosePartName(page: Page, name?: string) {
  await page.getByRole('dialog').getByRole('combobox', { name: /^Name/ }).click();
  await (name ? page.getByRole('option', { name, exact: true }) : page.getByRole('option').first()).click();
}

/** Opens the More menu of the record action bar (Archive, Delete and other rare actions live there). */
export const more = async (p: Page, scope = view(p)) => { await scope.getByTestId('more-actions').first().click(); return p.getByRole('menu'); };
