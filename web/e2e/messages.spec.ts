import { test, expect, type Browser, type Page } from '@playwright/test';
import { signIn, view } from './support';

async function json(page: Page, method: string, url: string, body?: unknown) {
  const r = await page.request.fetch(url, { method, data: body, headers: body === undefined ? undefined : { 'Content-Type': 'application/json' } });
  if (!r.ok()) throw new Error(`${method} ${url} ${r.status()} ${await r.text()}`);
  const text = await r.text();
  return text ? JSON.parse(text) : undefined;
}

async function person(admin: Page, name: string, permissions: string[]) {
  const stamp = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  const email = `${name.split(' ')[0].toLowerCase()}.${stamp}@test.local`;
  const password = 'long-enough-password';
  const userId = await json(admin, 'POST', '/api/admin/users', { email, displayName: name, password, mustChangePassword: false });
  const roleId = await json(admin, 'POST', '/api/admin/roles', { name: `Msg ${stamp}`, description: null, permissions });
  await json(admin, 'POST', `/api/admin/users/${userId}/roles`, { roleId, siteId: null, departmentId: null });
  return { email, password, userId: userId as string };
}

async function signInAs(browser: Browser, email: string, password: string) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('/');
  await page.fill('input[type=email]', email);
  await page.fill('input[type=password]', password);
  await page.click('button[type=submit]');
  await expect(page.getByTestId('tab-strip')).toBeVisible();
  return { context, page };
}

const nav = (page: Page) => page.locator('[data-testid="section-Messages"]:visible').first();

test('send, reply, unread badge, group thread and a person without permission', async ({ page, browser }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  const caseyName = `Casey ${stamp}`;
  const rileyName = `Riley ${stamp}`;
  const casey = await person(page, caseyName, ['messages.use']);
  const riley = await person(page, rileyName, ['messages.use']);
  const locked = await person(page, `Locked ${stamp}`, ['sequfloh.access']);

  await nav(page).click();
  await view(page).getByTestId('new-message').click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('To').fill(caseyName);
  await page.getByRole('option', { name: caseyName }).click();
  await dialog.getByText('New message', { exact: true }).click();
  await dialog.getByLabel('Message').fill('hello from the office');
  await dialog.getByTestId('new-message-send').click();
  await expect(dialog).toBeHidden();
  await expect(view(page).getByTestId('message-item').filter({ hasText: 'hello from the office' })).toBeVisible();
  await expect(view(page).getByTestId('message-when').first()).not.toBeEmpty();

  const caseySession = await signInAs(browser, casey.email, casey.password);
  await expect(nav(caseySession.page)).toHaveAttribute('data-unread-count', '1', { timeout: 20_000 });
  await nav(caseySession.page).click();
  await view(caseySession.page).getByTestId('conversation-item').filter({ hasText: 'hello from the office' }).click();
  await expect(view(caseySession.page).getByTestId('message-item').filter({ hasText: 'hello from the office' })).toBeVisible();
  await expect(view(caseySession.page).getByTestId('message-item').filter({ hasText: 'hello from the office' }).getByTestId('message-when')).not.toBeEmpty();
  await view(caseySession.page).getByTestId('reply-body').fill('hello back');
  await view(caseySession.page).getByTestId('reply-body-send').click();
  await expect(view(caseySession.page).getByTestId('message-item').filter({ hasText: 'hello back' })).toBeVisible();
  await expect(nav(caseySession.page)).toHaveAttribute('data-unread-count', '0', { timeout: 20_000 });

  await expect(view(page).getByTestId('message-log').getByText('hello back')).toBeVisible({ timeout: 20_000 });

  await view(page).getByTestId('new-message').click();
  const group = page.getByRole('dialog');
  await group.getByLabel('To').fill(caseyName);
  await page.getByRole('option', { name: caseyName }).click();
  await group.getByLabel('To').fill(rileyName);
  await page.getByRole('option', { name: rileyName }).click();
  await group.getByText('New message', { exact: true }).click();
  await group.getByTestId('group-name').fill('Floor notes');
  await group.getByLabel('Message').fill('group hello');
  await group.getByTestId('new-message-send').click();
  await expect(group).toBeHidden();
  await expect(view(page).getByTestId('conversation-item').filter({ hasText: 'Floor notes' })).toBeVisible();

  await expect(view(caseySession.page).getByTestId('conversation-item').filter({ hasText: 'Floor notes' })).toBeVisible({ timeout: 20_000 });
  await view(caseySession.page).getByTestId('conversation-item').filter({ hasText: 'Floor notes' }).click();
  await expect(view(caseySession.page).getByTestId('message-item').filter({ hasText: 'group hello' })).toBeVisible();
  await caseySession.context.close();

  const lockedSession = await signInAs(browser, locked.email, locked.password);
  await expect(lockedSession.page.locator('[data-testid="section-Messages"]')).toHaveCount(0);
  const denied = await lockedSession.page.request.get('/api/messages/conversations');
  expect(denied.status()).toBe(403);
  await lockedSession.context.close();
});
