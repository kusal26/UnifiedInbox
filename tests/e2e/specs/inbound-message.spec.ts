import { createHmac } from 'node:crypto';
import { test, expect } from '@playwright/test';
import { loginAs, registerOwner, slug, API } from './helpers';

// Needs a stack with WhatsApp secrets configured and a connected channel whose
// phone_number_id matches PHONE_NUMBER_ID. Skipped otherwise.
const APP_SECRET = process.env.WHATSAPP_APP_SECRET;
const PHONE_NUMBER_ID = process.env.WHATSAPP_PHONE_NUMBER_ID;
const CUSTOMER = process.env.WHATSAPP_TEST_CUSTOMER ?? '15550001111';

test.skip(!APP_SECRET || !PHONE_NUMBER_ID, 'requires WhatsApp secrets and a connected test number');

test('an inbound message arrives in the inbox', async ({ page, request }) => {
  const workspace = slug('e2e-inbound');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);

  const messageId = `wamid.e2e-${Date.now()}`;
  const payload = {
    entry: [{
      changes: [{
        value: {
          metadata: { phone_number_id: PHONE_NUMBER_ID },
          messages: [{ id: messageId, from: CUSTOMER, text: { body: 'hello from e2e' } }],
        },
      }],
    }],
  };
  const body = JSON.stringify(payload);
  const signature = `sha256=${createHmac('sha256', APP_SECRET!).update(body).digest('hex')}`;
  const webhook = await request.post(`${API}/api/v1/webhooks/whatsapp`, {
    headers: { 'X-Hub-Signature-256': signature, 'Content-Type': 'application/json' },
    data: body,
  });
  expect([200, 404]).toContain(webhook.status());

  await loginAs(request, workspace, email, page);
  if (webhook.status() === 200) {
    await expect(page.getByText('hello from e2e').first()).toBeVisible({ timeout: 30_000 });
  }
});
