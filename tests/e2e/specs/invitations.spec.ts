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

test('owner can promote a member and deactivate and reactivate them', async ({ page, request }) => {
  const workspace = slug('e2e-role');
  const ownerEmail = `owner-${workspace}@example.com`;
  const memberEmail = `member-${workspace}@example.com`;
  await registerOwner(request, workspace, ownerEmail);

  // Invite and accept over the API so the team has a second member.
  const invite = await request.post(`${API}/api/v1/invitations`, {
    headers: { Authorization: `Bearer ${await loginAs(request, workspace, ownerEmail)}` },
    data: { email: memberEmail, role: 'Agent' },
  });
  expect(invite.ok()).toBeTruthy();
  const token = await mailpitToken(request, memberEmail);
  const accept = await request.post(`${API}/api/v1/invitations/accept`, {
    data: { token, displayName: 'Member', password: PASSWORD },
  });
  expect(accept.ok()).toBeTruthy();

  await loginAs(request, workspace, ownerEmail, page);
  await page.getByRole('link', { name: 'Team' }).click();
  const memberRow = page.locator('tr').filter({ hasText: memberEmail });
  await memberRow.getByRole('button', { name: 'Deactivate' }).click();
  await expect(memberRow.getByText('Disabled')).toBeVisible();
  await memberRow.getByRole('button', { name: 'Reactivate' }).click();
  await expect(memberRow.getByText('Active')).toBeVisible();
});

test('revoked invitations can no longer be accepted', async ({ page, request }) => {
  const workspace = slug('e2e-revoke');
  const ownerEmail = `owner-${workspace}@example.com`;
  const memberEmail = `member-${workspace}@example.com`;
  await registerOwner(request, workspace, ownerEmail);
  const ownerToken = await loginAs(request, workspace, ownerEmail);

  const invite = await request.post(`${API}/api/v1/invitations`, {
    headers: { Authorization: `Bearer ${ownerToken}` },
    data: { email: memberEmail, role: 'Agent' },
  });
  expect(invite.ok()).toBeTruthy();
  const invitationId = (await invite.json()).id as string;
  const token = await mailpitToken(request, memberEmail);

  const revoke = await request.delete(`${API}/api/v1/invitations/${invitationId}`, {
    headers: { Authorization: `Bearer ${ownerToken}` },
  });
  expect(revoke.ok()).toBeTruthy();

  const accept = await request.post(`${API}/api/v1/invitations/accept`, {
    data: { token, displayName: 'Member', password: PASSWORD },
  });
  expect(accept.status()).toBe(400);
});

test('expired invitations are rejected', async ({ request }) => {
  const response = await request.post(`${API}/api/v1/invitations/accept`, {
    data: { token: 'not-a-real-token', displayName: 'Nobody', password: PASSWORD },
  });
  expect(response.status()).toBe(400);
});
