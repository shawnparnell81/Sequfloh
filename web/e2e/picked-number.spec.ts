import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

/** Picking a part by its NAPA US # shows that number in the dropdown and saves it as the request's part number. Made-up data only. */
test('the number picked for a part is the part number on the request', async ({ page }) => {
  const ok = async (r: Promise<import('@playwright/test').APIResponse>) => { const x = await r; expect(x.ok(), await x.text()).toBeTruthy(); return x.status() === 204 ? null : x.json(); };
  const k = Date.now().toString(36).toUpperCase();
  await signIn(page);
  const rq = page.request;
  const site = (await ok(rq.get('/api/organization/sites')))[0].id as string;
  await ok(rq.post('/api/sequfloh/parts', { data: {
    part: { partNumber: `PK-${k}`, name: 'Example picked part', partType: 1, revision: 'A', unitOfMeasure: 'EA', siteId: site, status: 0, hasSpecialCharacteristics: false },
    execution: { productFamily: null, isRepairable: false, isSerialized: false, isLotControlled: false }, attributes: { napaUs: `NU${k}` } } }));

  await page.goto('/sequfloh');
  await view(page).getByRole('button', { name: 'New inspection request' }).click();
  const dlg = page.getByRole('dialog');
  await dlg.getByTestId('ir-number').fill(`IR-${k}`);
  await dlg.getByLabel('Part number').fill(`nu${k.toLowerCase()}`);
  const opt = page.getByRole('option', { name: new RegExp(`NU${k} \\(NAPA\\) · PK-${k}`) });
  await expect(opt).toBeVisible();
  await opt.click();
  await expect(dlg.getByLabel('Part number')).toHaveValue(`NU${k}`);
  await dlg.getByRole('button', { name: 'Submit request' }).click();
  await expect(page).toHaveURL(/inspection-request\//);
  const id = page.url().split('/').pop()!;
  const r = await ok(rq.get(`/api/sequfloh/inspection-requests/${id}`));
  expect(r.partNumber ?? r.request?.partNumber).toBe(`NU${k}`);
});
