import { test, expect, type Browser, type Page } from '@playwright/test';
import { signIn, view } from './support';

async function json(page: Page, method: string, url: string, body?: unknown) {
  const r = await page.request.fetch(url, { method, data: body, headers: body === undefined ? undefined : { 'Content-Type': 'application/json' } });
  if (!r.ok()) throw new Error(`${method} ${url} ${r.status()} ${await r.text()}`);
  const text = await r.text();
  return text ? JSON.parse(text) : undefined;
}

async function person(admin: Page, name: string) {
  const stamp = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  const email = `${name.split(' ')[0].toLowerCase()}.${stamp}@test.local`;
  const password = 'long-enough-password';
  const userId = await json(admin, 'POST', '/api/admin/users', { email, displayName: name, password, mustChangePassword: false });
  const roleId = await json(admin, 'POST', '/api/admin/roles', { name: `Rich ${stamp}`, description: null, permissions: ['messages.use', 'sequfloh.access'] });
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

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');

test('attachments, work order card, reactions and an online dot', async ({ page, browser }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  const caseyName = `Casey ${stamp}`;
  const casey = await person(page, caseyName);
  const site = (await json(page, 'GET', '/api/organization/sites'))[0].id as string;
  const part = await json(page, 'POST', '/api/sequfloh/parts', {
    part: { partNumber: `RICH-${stamp}`, name: 'Made-up bracket', description: 'bracket', partType: 0, revision: 'A', unitOfMeasure: 'EA', productId: null, material: '', hasSpecialCharacteristics: false, siteId: site, departmentId: null, status: 0, rowVersion: 0 },
    execution: { productFamily: null, assemblyType: null, isRepairable: false, isSerialized: false, isLotControlled: false, drawingNumber: null, specification: null },
    typeId: null, attributes: null,
  });
  const wo = await json(page, 'POST', '/api/sequfloh/tracking', { type: 'Rebox', siteId: site, partId: part.id, number: `RICH-${stamp}` });
  await json(page, 'PUT', `/api/sequfloh/tracking/${wo.id}/job/findings`, { supplier: 'Made-up Metals', hasFindings: true });

  await page.locator('[data-testid="section-Messages"]:visible').first().click();
  await view(page).getByTestId('new-message').click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('To').fill(caseyName);
  await page.getByRole('option', { name: caseyName }).click();
  await dialog.getByText('New message', { exact: true }).click();
  await dialog.getByLabel('Message').fill('parts are in');
  await dialog.getByRole('button', { name: 'Link a work order' }).click();
  await page.getByTestId('message-work-order').fill(`RICH-${stamp}`);
  await page.getByRole('option', { name: new RegExp(`RICH-${stamp}`) }).click();
  await dialog.locator('input[type=file]').setInputFiles([
    { name: 'tiny.png', mimeType: 'image/png', buffer: png },
    { name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('hello file') },
  ]);
  await dialog.getByTestId('new-message-send').click();
  await expect(dialog).toBeHidden();

  const item = view(page).getByTestId('message-item').filter({ hasText: 'parts are in' });
  await expect(item).toBeVisible();
  await expect(item.getByTestId('message-attachment').filter({ has: page.locator('[data-kind=image], img') }).or(item.locator('[data-testid="message-attachment"][data-kind="image"]'))).toBeVisible();
  await expect(item.locator('[data-testid="message-attachment"][data-kind="file"]')).toHaveText('notes.txt');
  await expect(item.getByTestId('message-link')).toContainText('Made-up Metals');
  await expect(item.getByTestId('message-link')).toContainText(`RICH-${stamp}`);

  await item.getByTestId('emoji-button').click();
  await expect(view(page).getByTestId('emoji-picker').or(page.getByTestId('emoji-picker'))).toBeVisible();
  await page.getByTestId('emoji-picker').getByRole('button', { name: '👍' }).click();
  await expect(item.getByTestId('message-reaction')).toContainText('👍');

  const caseySession = await signInAs(browser, casey.email, casey.password);
  await expect(caseySession.page.getByTestId('user-menu').getByTestId('avatar-presence')).toHaveAttribute('data-online', 'true');
  await caseySession.page.locator('[data-testid="section-Messages"]:visible').first().click();
  await view(caseySession.page).getByTestId('conversation-item').filter({ hasText: 'parts are in' }).click();
  await expect(view(caseySession.page).getByTestId('message-item').getByTestId('avatar-presence').first()).toBeVisible();
  await caseySession.context.close();
});
