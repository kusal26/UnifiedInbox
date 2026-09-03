import { test, expect } from '@playwright/test';
import { loginAs, registerOwner, slug } from './helpers';

const routes = [
  ['Shared Inbox', '/'],
  ['Overview', '/overview'],
  ['Channels', '/channels'],
  ['Team', '/team'],
  ['Canned Responses', '/canned'],
  ['Notifications', '/notifications'],
  ['Audit Log', '/audit'],
  ['Settings', '/settings'],
] as const;

test('every workspace route loads against the real API', async ({ page, request }) => {
  const workspace = slug('e2e-routes');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  for (const [label, path] of routes) {
    await page.goto(path);
    await expect(page.getByRole('heading', { name: label, exact: true })).toBeVisible();
  }
});

test('login requires a known tenant workspace', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Workspace slug').fill('unknown-workspace');
  await page.getByLabel(/^Email/).fill('nobody@example.com');
  await page.getByLabel(/^Password/).fill('e2e-test-password-1');
  await page.getByRole('button', { name: 'Open inbox' }).click();
  await expect(page.getByRole('alert')).toBeVisible();
});
