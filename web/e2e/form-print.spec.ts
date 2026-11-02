import { expect, test, type Page } from '@playwright/test';
import { signIn, view } from './support';

/** Every form being viewed has the same Print action, and the printout is the form alone with its header. Made-up records only. */
const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
const upload = async (page: Page, site: string, name: string, body: string, flag?: string) =>
  (await ok(page.request.post('/api/imported-files', { multipart: { siteId: site, ...(flag ? { [flag]: 'true' } : {}), file: { name, mimeType: 'text/csv', buffer: Buffer.from(body) } } }))).id as string;

test('form print: Print on a completed validation form and another form, and the printout is only the form', async ({ page }) => {
  await signIn(page);
  const stamp = Date.now().toString(36);
  const site = (await ok(page.request.get('/api/organization/sites')) as { id: string }[])[0].id;
  const families = await ok(page.request.get('/api/sequfloh/reference/product-families')) as { id: string }[];
  const validation = await upload(page, site, `Validation ${stamp}.csv`,
    'Characteristic,Min,Max,Reading,Result\nTorque,25,30,27,PASS\nLength,10,12,11.2,PASS\nSigned by,Jane Example,2026-11-02 07:45,,\n', 'completedForm');
  if (families[0]) await ok(page.request.put(`/api/imported-files/${validation}/family`, { data: { familyId: families[0].id } }));
  const other = await upload(page, site, `Checklist ${stamp}.csv`, 'Item,Done\nGuard in place,Yes\nInspector,Sam Example\n');

  const shots = process.env.PRINT_SHOTS;
  for (const id of [validation, other]) {
    await page.goto(`/sequfloh/imported-files/${id}`);
    const print = view(page).locator('[data-record-actions] [data-action="print"]');
    await expect(print).toBeVisible();
    const [win] = await Promise.all([page.waitForEvent('popup'), print.click()]);
    await win.waitForLoadState();
    expect(win.url()).toContain(`/forms/fill/${id}?print=1`);
    await expect(win.getByTestId('form-window')).toBeVisible();
    await expect(win.getByTestId('form-print')).toBeVisible();
    if (shots) { await win.setViewportSize({ width: 1300, height: 850 }); await win.waitForTimeout(500); await win.screenshot({ path: `${shots}/${id === validation ? '03-validation-form-window' : '02-other-form-window'}.png` }); }
    await win.emulateMedia({ media: 'print' });
    await expect(win.getByTestId('form-print')).toBeHidden();
    await expect(win.getByTestId('zoom-fit')).toBeHidden();
    await expect(win.getByTestId('form-print-header')).toBeVisible();
    await expect(win.getByTestId('sheet-grid')).toBeVisible();
    if (id === validation) await expect(win.getByTestId('sheet-grid')).toContainText('Jane Example');
    if (shots && id === validation) {
      await win.screenshot({ path: `${shots}/04-print-preview.png`, fullPage: true });
      await win.pdf({ path: `${shots}/05-printed-validation-form.pdf`, format: 'Letter', printBackground: true });
    }
    await win.close();
  }

  // full screen keeps the same Print action
  await page.goto(`/sequfloh/imported-files/${validation}`);
  await view(page).getByTestId('form-fullscreen').click();
  await expect(view(page).getByTestId('form-view').getByTestId('form-print')).toBeVisible();
  if (shots) { await page.waitForTimeout(500); await page.screenshot({ path: `${shots}/01-validation-form-full-screen.png` }); }
});
