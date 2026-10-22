import { test, expect } from '@playwright/test';
import { section, signIn, strip, tabs, view } from './support';

test.describe('workspace tabs', () => {
  test('sections open in their own tabs, keep typing, ask before closing unsaved work and come back after a reload', async ({ page }) => {
    await signIn(page);
    await expect(tabs(page)).toHaveCount(1);
    await section(page, 'Parts and Items');
    await expect(view(page).getByTestId('section-menu')).toContainText('Part Master');
    await section(page, 'Tracking');
    await section(page, 'Reports');
    await expect(tabs(page)).toHaveCount(4);

    // typing stays in its tab
    await strip(page).locator('[data-tab-url="/sequfloh/parts"]').click();
    const box = view(page).getByRole('textbox').first();
    await box.fill('housing');
    await strip(page).locator('[data-tab-url^="/sequfloh/reports"]').click();
    await strip(page).locator('[data-tab-url="/sequfloh/parts"]').click();
    await expect(view(page).getByRole('textbox').first()).toHaveValue('housing');

    // Back belongs to the tab
    await view(page).getByTestId('section-page').filter({ hasText: 'Parts Lists' }).click();
    await expect(page).toHaveURL(/\/sequfloh\/parts-lists$/);
    await view(page).getByTestId('page-back').click();
    await expect(page).toHaveURL(/\/sequfloh\/parts$/);
    await expect(strip(page).locator('[data-tab-url="/sequfloh/parts"]')).toHaveCount(1);

    // the order and the tabs survive a reload
    const before = await tabs(page).evaluateAll((e) => e.map((x) => x.getAttribute('data-tab-url')));
    await page.reload();
    await expect(tabs(page)).toHaveCount(before.length);
    await expect.poll(() => tabs(page).evaluateAll((e) => e.map((x) => x.getAttribute('data-tab-url')))).toEqual(before);

    // closing a record tab with unsaved typing asks first
    const site = (await (await page.request.get('/api/organization/sites')).json())[0].id;
    const title = `Tab draft ${Date.now()}`;
    await page.request.post('/api/sequfloh/instructions', { data: { type: 'Validation', title, siteId: site } });
    await section(page, 'Work Instructions');
    await view(page).getByTestId('section-page').filter({ hasText: 'Draft Instructions' }).click();
    await view(page).locator('tbody tr', { hasText: title }).click({ modifiers: ['Control'] });
    const rec = strip(page).locator('[data-tab-url*="/sequfloh/instruction/"]').last();
    await rec.click();
    const field = view(page).getByLabel('Title', { exact: true });
    await field.fill('Changed but not saved');
    await strip(page).locator('[data-tab-url^="/sequfloh/instructions/"]').first().click();
    await rec.click();
    await expect(view(page).getByLabel('Title', { exact: true })).toHaveValue('Changed but not saved');
    await rec.getByTestId('tab-close').click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(rec).toBeVisible();
  });

  test('ctrl-click opens a row in another tab and the right-click menu offers the same', async ({ page }) => {
    await signIn(page);
    await section(page, 'Work Instructions');
    const row = view(page).locator('tbody tr').first();
    await row.click({ modifiers: ['Control'] });
    await expect(tabs(page)).toHaveCount(3);
    await row.click({ button: 'right' });
    await page.getByTestId('open-in-new-tab').click();
    await expect(tabs(page).count()).resolves.toBeGreaterThanOrEqual(3);
  });

  test('tabs can be switched off per person', async ({ page }) => {
    await signIn(page);
    await page.goto('/account/preferences');
    await page.getByRole('switch', { name: 'Use tabs' }).uncheck();
    await expect(page.getByTestId('tab-strip')).toHaveCount(0);
    await page.getByRole('switch', { name: 'Use tabs' }).check();
    await expect(page.getByTestId('tab-strip')).toBeVisible();
  });
});
