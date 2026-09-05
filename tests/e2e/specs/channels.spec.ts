import { test, expect } from '@playwright/test';
import { API, loginAs, mailpitToken, PASSWORD, registerOwner, slug } from './helpers';

test('the channels page starts Meta Embedded Signup with no manual credential fields', async ({ page, request }) => {
  const workspace = slug('e2e-channels');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);
  await loginAs(request, workspace, email, page);

  await page.getByRole('link', { name: 'Channels' }).click();
  await expect(page.getByRole('heading', { name: 'Channels' })).toBeVisible();

  // Completing the wizard is a server + Meta popup flow; starting it surfaces the SDK handshake.
  await page.getByLabel('Channel display name').fill('Sales line');
  await page.getByRole('button', { name: 'Start Embedded Signup' }).click();
  await expect(page.getByText(/Complete Meta Embedded Signup in the popup/)).toBeVisible();
  await expect(page.getByRole('button', { name: /Continue in the Meta popup/ })).toBeVisible();

  // The old manual authorization-code, phone-number-ID, and business-ID fields are gone.
  await expect(page.getByLabel('Authorization code')).toHaveCount(0);
  await expect(page.getByLabel('Phone number ID')).toHaveCount(0);
  await expect(page.getByLabel('Business ID')).toHaveCount(0);
});

test('agents do not see the connect-a-channel controls', async ({ page, request }) => {
  const workspace = slug('e2e-channel-agent');
  const ownerEmail = `owner-${workspace}@example.com`;
  const agentEmail = `agent-${workspace}@example.com`;
  await registerOwner(request, workspace, ownerEmail);
  const ownerToken = await loginAs(request, workspace, ownerEmail);
  const invite = await request.post(`${API}/api/v1/invitations`, {
    headers: { Authorization: `Bearer ${ownerToken}` },
    data: { email: agentEmail, role: 'Agent' },
  });
  expect(invite.ok()).toBeTruthy();
  const token = await mailpitToken(request, agentEmail);
  await request.post(`${API}/api/v1/invitations/accept`, { data: { token, displayName: 'Agent', password: PASSWORD } });

  await loginAs(request, workspace, agentEmail, page);
  await expect(page.getByRole('link', { name: 'Channels' })).toHaveCount(0);
  await page.goto('/channels');
  await expect(page.getByText('Connect a WhatsApp number')).toHaveCount(0);
});
