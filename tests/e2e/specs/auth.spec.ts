import { test, expect } from '@playwright/test';
import { loginAs, mailpitToken, PASSWORD, registerOwner, slug } from './helpers';

test('registration flows through verification into login', async ({ page, request }) => {
  const workspace = slug('e2e-auth');
  const email = `${workspace}@example.com`;

  await page.goto('/register');
  await page.getByLabel(/Workspace name/).fill(workspace);
  await page.getByLabel(/Workspace slug/).fill(workspace);
  await page.getByLabel(/Your name/).fill('Owner');
  await page.getByLabel(/^Email/).fill(email);
  await page.getByLabel(/^Password/).fill(PASSWORD);
  await page.getByRole('button', { name: 'Create workspace' }).click();
  await expect(page.getByRole('heading', { name: 'Verify your email' })).toBeVisible();

  const token = await mailpitToken(request, email);
  await page.getByLabel('Verification token').fill(token);
  await page.getByRole('button', { name: 'Verify email' }).click();
  await expect(page.getByText(/Your email is verified/)).toBeVisible();

  await loginAs(request, workspace, email, page);
  await expect(page.getByText(workspace, { exact: false }).first()).toBeVisible();
});

test('forgot and reset password restores access', async ({ page, request }) => {
  const workspace = slug('e2e-reset');
  const email = `${workspace}@example.com`;
  await registerOwner(request, workspace, email);

  await page.goto('/forgot-password');
  await page.getByLabel(/^Email/).fill(email);
  await page.getByRole('button', { name: 'Send reset email' }).click();
  await expect(page.getByText(/If the account exists/)).toBeVisible();

  const token = await mailpitToken(request, email);
  await page.goto('/reset-password');
  await page.getByLabel('Reset token').fill(token);
  await page.getByLabel(/New password/).fill('e2e-test-password-2');
  await page.getByRole('button', { name: 'Reset password' }).click();
  await expect(page.getByText(/Your password was reset/)).toBeVisible();
});
