import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { settledShot, signIn, view } from './support';

/** Every page reachable from the menus, read from the registry so a new page is checked without touching this test. */
const registry = fs.readFileSync(path.resolve('src/modules/registry.tsx'), 'utf8');
const PAGES = [...new Set([...registry.matchAll(/path: '([^']+)'/g)].map((m) => m[1]).concat(['/account/preferences', '/account/password', '/sequfloh/search?q=test']))];

/**
 * One layout everywhere: each page renders the shared page header (breadcrumb, one title, subtitle/meta, right-aligned actions) exactly once,
 * no page draws its own title, destructive actions are never loose buttons in a header, and list search rows use the shared list toolbar.
 */
test('every page uses the shared page header', async ({ page }) => {
  test.setTimeout(300_000);
  const shots = process.env.E2E_SHOTS;
  await signIn(page);
  for (const url of PAGES) {
    await page.goto(url);
    const v = view(page);
    await expect(v.locator('[data-page-header]'), url).toHaveCount(1);
    await expect(v.locator('[data-page-header] h1'), url).toBeVisible();
    await expect(v.locator('h1'), `${url}: one title`).toHaveCount(1);
    await expect(v.locator('[data-page-actions]').getByRole('button', { name: /^(Delete|Archive)\b/ }), `${url}: destructive actions belong in More`).toHaveCount(0);
    // a search row on the page is the shared list toolbar
    const searchButtons = v.getByRole('button', { name: 'Search', exact: true });
    for (let i = 0; i < await searchButtons.count(); i++) {
      const inDialog = await searchButtons.nth(i).evaluate((b) => !!b.closest('[role="dialog"]'));
      if (!inDialog) await expect(searchButtons.nth(i).locator('xpath=ancestor::*[@data-list-toolbar]'), `${url}: search row`).toHaveCount(1);
    }
    if (shots) await settledShot(page, path.join(shots, `page-${url.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '')}.png`));
  }
});

/** No page draws its own big title or ad-hoc search form: they come from PageHeader and ListToolbar. */
test('no ad-hoc page titles or search rows in the source', () => {
  const root = path.resolve('src');
  const walk = (d: string): string[] => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => e.isDirectory() ? walk(path.join(d, e.name)) : /\.tsx$/.test(e.name) ? [path.join(d, e.name)] : []);
  const files = walk(root);
  const allowH1 = [path.join('layout', 'PageHeader.tsx'), path.join('pages', 'SignIn.tsx')]; // the sign-in card is shown before any page
  expect(files.filter((f) => !allowH1.some((a) => f.endsWith(a)) && /variant="h1"/.test(fs.readFileSync(f, 'utf8'))).map((f) => path.relative(root, f))).toEqual([]);
  expect(files.filter((f) => !f.endsWith(path.join('layout', 'PageHeader.tsx')) && />Search<\/Button>/.test(fs.readFileSync(f, 'utf8')) && !/ListToolbar/.test(fs.readFileSync(f, 'utf8'))).map((f) => path.relative(root, f))).toEqual([]);
});
