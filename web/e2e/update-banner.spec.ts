import { expect, test } from '@playwright/test';
import { signIn } from './support';

/** After an update goes live, signed-in pages offer a refresh; nothing reloads on its own and a dismissed banner comes back on the next check. */
test('update banner: shown when a new version is live, dismissed, shown again, refresh reloads', async ({ page }) => {
  let version = 'build-one';
  await page.route('**/api/version', (r) => r.fulfill({ json: { version }, headers: { 'cache-control': 'no-store' } }));
  await signIn(page);
  await page.waitForTimeout(1000);
  const banner = page.getByTestId('update-banner');
  await expect(banner).toHaveCount(0);

  version = 'build-two';
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(banner).toContainText('A new version of Sequfloh is available. Refresh to get the latest update.');
  await page.waitForTimeout(500);
  expect(page.url()).not.toContain('v=');
  if (process.env.UPDATE_SHOTS) await page.screenshot({ path: `${process.env.UPDATE_SHOTS}/update-banner.png` });

  await page.getByTestId('update-dismiss').click();
  await expect(banner).toHaveCount(0);
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(banner).toBeVisible();

  await page.getByTestId('update-refresh').click();
  await page.waitForURL(/[?&]v=/);
  await page.waitForTimeout(1000);
  await expect(banner).toHaveCount(0);
});
