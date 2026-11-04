import { expect, test } from '@playwright/test';
import { signIn, view } from './support';

/** A made-up PDF the upload check accepts. Nothing in it is a real drawing. */
const pdf = (title: string, rev: string) => Buffer.from(`%PDF-1.4\n% made-up ${title} rev ${rev}\n%%EOF\n`);

test('drawing revisions: history, superseded banner, status filter, and release when approval is on', async ({ page, context }) => {
  const stamp = Date.now().toString().slice(-6);
  const dwg = `DWG-REV-${stamp}`;
  await signIn(page);
  const settings = await page.request.get('/api/sequfloh/settings');
  expect(settings.ok()).toBeTruthy();
  const body = await settings.json();
  const numbering = body.numbering ?? {};
  const restore = async (approval: boolean) => {
    const fresh = await (await page.request.get('/api/sequfloh/settings')).json();
    const put = await page.request.put('/api/sequfloh/settings/numbering', { data: { value: { ...numbering, revisionFormat: numbering.revisionFormat ?? 'Letters', drawingApprovalRequired: approval }, rowVersion: fresh.rowVersion } });
    expect(put.ok()).toBeTruthy();
  };
  try {
    await restore(false);
    await page.goto('/sequfloh/drawings');
    await view(page).getByRole('button', { name: 'New folder' }).click();
    await page.getByLabel('Folder name').fill(`Revisions ${stamp}`);
    await page.getByRole('button', { name: 'Save' }).click();
    await view(page).getByRole('button', { name: 'Upload drawing' }).click();
    await page.getByTestId('drawing-file').setInputFiles({ name: 'rev-a.pdf', mimeType: 'application/pdf', buffer: pdf('PLATE', 'A') });
    await page.getByLabel('Title').fill('Revision plate');
    await page.getByLabel('Drawing number').fill(dwg);
    await page.getByRole('button', { name: 'Upload', exact: true }).click();
    await expect(view(page).getByTestId('current-rev')).toHaveText('A');

    await view(page).getByRole('button', { name: 'New revision' }).click();
    await page.getByTestId('revision-file').setInputFiles({ name: 'rev-b.pdf', mimeType: 'application/pdf', buffer: pdf('PLATE', 'B') });
    await page.getByLabel('Reason for change').fill('Slot added');
    await page.getByLabel('Change reference').fill('ECN-44');
    await page.getByRole('button', { name: 'Upload revision' }).click();
    await expect(view(page).getByTestId('current-rev')).toHaveText('B');
    const history = view(page).getByTestId('revision-history');
    await expect(history).toContainText('Slot added');
    await expect(history).toContainText('ECN-44');
    await expect(history).toContainText('Superseded');
    const [old] = await Promise.all([context.waitForEvent('page'), history.getByRole('row', { name: /Superseded/ }).getByRole('link', { name: 'View' }).click()]);
    await expect(old.getByTestId('superseded-banner')).toContainText('superseded revision A');
    await expect(old.getByTestId('viewer-title')).toContainText('rev A');
    await old.close();

    await page.goto('/sequfloh/drawings');
    await view(page).getByLabel('Search drawings').fill(dwg);
    await view(page).getByLabel('Revision status').click();
    await page.getByRole('option', { name: 'Superseded' }).click();
    await view(page).getByRole('button', { name: 'Search' }).click();
    await expect(view(page).getByText('Revision plate')).toBeVisible();

    await restore(true);
    await page.goto(`/sequfloh/drawings`);
    await view(page).getByLabel('Search drawings').fill(dwg);
    await view(page).getByRole('button', { name: 'Search' }).click();
    await view(page).getByTestId('drawing-details').first().click();
    await view(page).getByRole('button', { name: 'New revision' }).click();
    await page.getByTestId('revision-file').setInputFiles({ name: 'rev-c.pdf', mimeType: 'application/pdf', buffer: pdf('PLATE', 'C') });
    await page.getByLabel('Reason for change').fill('Waiting on release');
    await page.getByRole('button', { name: 'Submit for release' }).click();
    await expect(view(page).getByTestId('current-rev')).toHaveText('B');
    await expect(view(page).getByTestId('pending-revision')).toContainText('pending release');
    const [pending] = await Promise.all([context.waitForEvent('page'), view(page).getByRole('row', { name: /Pending/ }).getByRole('link', { name: 'View' }).click()]);
    await expect(pending.getByTestId('pending-banner')).toContainText('pending release');
    await pending.close();
    await view(page).getByTestId('release-revision').click();
    await page.getByRole('dialog').getByRole('button', { name: 'Release' }).click();
    await expect(view(page).getByTestId('current-rev')).toHaveText('C');
    await expect(view(page).getByTestId('revision-history')).toContainText('Superseded');
  } finally {
    await restore(false);
  }
});
