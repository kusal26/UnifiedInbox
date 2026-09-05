import { test, expect } from '@playwright/test';
import { loginAs, registerOwner, slug } from './helpers';

test('owner manages canned responses end to end', async ({ page, request }) => {
  const workspace = slug('e2e-canned');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  await page.getByRole('link', { name: 'Canned Responses' }).click();
  await page.getByLabel('Title').fill('Shipping update');
  await page.getByLabel('Shortcut').fill('/shipping');
  await page.getByLabel('Content').fill('Your order shipped today.');
  await page.getByRole('button', { name: 'Create response' }).click();
  await expect(page.getByText('Shipping update')).toBeVisible();

  await page.getByRole('button', { name: 'Edit' }).click();
  await page.getByLabel('Edit content').fill('Your order shipped and is on its way.');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Your order shipped and is on its way.')).toBeVisible();

  await page.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Shipping update')).toHaveCount(0);
});

test('owner can update workspace settings and they persist', async ({ page, request }) => {
  const workspace = slug('e2e-settings');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  await page.getByRole('link', { name: 'Settings' }).click();
  const name = page.getByLabel('Workspace name');
  await name.fill(`${workspace}-renamed`);
  await page.getByRole('button', { name: 'Save settings' }).click();
  await expect(page.getByText('Settings saved.')).toBeVisible();

  await page.reload();
  await expect(page.getByLabel('Workspace name')).toHaveValue(`${workspace}-renamed`);
});

test('owner can toggle notification preferences', async ({ page, request }) => {
  const workspace = slug('e2e-prefs');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  await page.getByRole('link', { name: 'Notifications' }).click();
  await expect(page.getByRole('heading', { name: 'Notifications' })).toBeVisible();

  const row = page.locator('li', { hasText: 'Message delivery failures' });
  await row.getByRole('button', { name: 'Disable' }).click();
  await expect(row.getByRole('button', { name: 'Enable' })).toBeVisible();
});

test('owner can browse metrics overview', async ({ page, request }) => {
  const workspace = slug('e2e-metrics');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  await page.getByRole('link', { name: 'Overview' }).click();
  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
  await page.getByLabel('Metrics window').selectOption('7');
  await expect(page.getByText('Conversations opened')).toBeVisible();
});
