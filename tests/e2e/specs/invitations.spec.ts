import { test, expect } from '@playwright/test';
import { loginAs, mailpitToken, PASSWORD, registerOwner, slug, API } from './helpers';

test('owner invites a member who accepts and signs in', async ({ page, request }) => {
  const workspace = slug('e2e-invite');
  const ownerEmail = `owner-${workspace}@example.com`;
  const memberEmail = `member-${workspace}@example.com`;
  await registerOwner(request, workspace, ownerEmail);
  await loginAs(request, workspace, ownerEmail, page);

  await page.getByRole('link', { name: 'Team' }).click();
  await page.getByLabel('Invite email').fill(memberEmail);
  await page.getByRole('button', { name: 'Send invitation' }).click();
  await expect(page.getByText(`Invitation sent to ${memberEmail}.`)).toBeVisible();
  await expect(page.getByText(memberEmail)).toBeVisible();

  const token = await mailpitToken(request, memberEmail);
  await page.goto('/invitations/accept');
  await page.getByLabel('Invitation token').fill(token);
  await page.getByLabel(/Your name/).fill('Member');
  await page.getByLabel(/^Password/).fill(PASSWORD);
  await page.getByRole('button', { name: 'Join workspace' }).click();

  await loginAs(request, workspace, memberEmail, page);
  await expect(page.getByRole('heading', { name: 'Conversations' })).toBeVisible();
  // Agents see a reduced navigation surface.
  await expect(page.getByRole('link', { name: 'Audit Log' })).toHaveCount(0);
});

test('expired invitations are rejected', async ({ request }) => {
  const response = await request.post(`${API}/api/v1/invitations/accept`, {
    data: { token: 'not-a-real-token', displayName: 'Nobody', password: PASSWORD },
  });
  expect(response.status()).toBe(400);
});
