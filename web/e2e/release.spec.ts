import { test, expect } from '@playwright/test';
import { section, signIn, view } from './support';

test('a draft instruction is released with one button and the release shows in the audit trail', async ({ page }) => {
  await signIn(page);
  const site = (await (await page.request.get('/api/organization/sites')).json())[0].id;
  const title = `Release check ${Date.now()}`;
  const { id } = await (await page.request.post('/api/sequfloh/instructions', { data: { type: 'Validation', title, siteId: site } })).json();
  const url = `/api/sequfloh/instructions/${id}`;
  const wi = (await (await page.request.get(url)).json()).instruction;
  const saved = await page.request.put(url, { data: { title, siteId: site, departmentId: null, ownerId: null, partId: null, repairDescription: null, effectiveDate: null,
    content: { purpose: { purpose: 'Check the release step.' }, validationAcceptance: { overallAcceptanceCriteria: 'No leak at 150 psi' } },
    steps: [{ sequence: 10, title: 'Pressurize to test pressure', target: 150, lowerLimit: 145, upperLimit: 155, unit: 'psi' }], characteristics: [], partsRows: [], rowVersion: wi.rowVersion ?? 0 } });
  expect(saved.ok()).toBeTruthy();
  await section(page, 'Work Instructions');
  await view(page).getByTestId('section-page').filter({ hasText: 'Draft Instructions' }).click();
  await view(page).locator('tbody tr', { hasText: title }).click();
  await expect(page).toHaveURL(new RegExp(id));
  for (const name of ['Submit for review', 'Approve', 'Reject']) await expect(view(page).getByRole('button', { name })).toHaveCount(0);
  await view(page).getByRole('button', { name: /^Release/ }).first().click();
  const dlg = page.getByRole('dialog');
  const date = dlg.locator('input[type=date]');
  if (await date.count()) await date.fill(new Date().toISOString().slice(0, 10));
  await dlg.getByRole('button', { name: 'Release' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(view(page).getByText('Released').first()).toBeVisible();
  await page.goto('/account/preferences?audit=instructions');
  const number = (await (await page.request.get(url)).json()).instruction.number;
  await expect(view(page)).toContainText(`${number} rev A: draft -> released`);
  await expect(view(page)).toContainText('Status: Draft → Released');
});
