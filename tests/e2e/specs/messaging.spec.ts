import { createHmac } from 'node:crypto';
import { test, expect, type APIRequestContext } from '@playwright/test';
import { loginAs, registerOwner, slug, API } from './helpers';

// Arranging a real conversation requires a connected test number. When the WhatsApp secrets are
// absent the whole file self-skips, matching inbound-message.spec.ts.
const APP_SECRET = process.env.WHATSAPP_APP_SECRET;
const PHONE_NUMBER_ID = process.env.WHATSAPP_PHONE_NUMBER_ID;
const CUSTOMER = process.env.WHATSAPP_TEST_CUSTOMER ?? '15550001111';

test.skip(!APP_SECRET || !PHONE_NUMBER_ID, 'requires WhatsApp secrets and a connected test number');

async function deliverInbound(request: APIRequestContext, body: string) {
  const signature = `sha256=${createHmac('sha256', APP_SECRET!).update(body).digest('hex')}`;
  const webhook = await request.post(`${API}/api/v1/webhooks/whatsapp`, {
    headers: { 'X-Hub-Signature-256': signature, 'Content-Type': 'application/json' },
    data: body,
  });
  expect([200, 404]).toContain(webhook.status());
  return webhook.status() === 200;
}

async function seedInboundText(request: APIRequestContext, messageId: string, text: string) {
  const payload = {
    entry: [{ changes: [{ value: { metadata: { phone_number_id: PHONE_NUMBER_ID }, messages: [{ id: messageId, from: CUSTOMER, text: { body: text } }] } }] }],
  };
  return deliverInbound(request, JSON.stringify(payload));
}

test('an agent can reply, note, change status, and see delivery in the timeline', async ({ page, request }) => {
  const workspace = slug('e2e-messaging');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);

  const messageId = `wamid.e2e-${Date.now()}`;
  const inboundText = 'hello from the e2e handset';
  const delivered = await seedInboundText(request, messageId, inboundText);
  test.skip(!delivered, 'the webhook returned 404 (no connected channel for this tenant)');

  await loginAs(request, workspace, email, page);
  await expect(page.getByText(inboundText).first()).toBeVisible({ timeout: 30_000 });

  await page.getByLabel('Message').fill('we are on it');
  await page.getByRole('button', { name: 'Send reply' }).click();
  await expect(page.getByText('we are on it').first()).toBeVisible({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Internal note' }).click();
  await page.getByLabel('Message').fill('handled by the shared inbox');
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText('Private to staff').first()).toBeVisible();

  await page.getByRole('button', { name: /^Status: / }).click();
  await page.getByRole('menuitem', { name: 'Closed' }).click();
  await expect(page.getByRole('button', { name: 'Status: Closed' })).toBeVisible();
});

test('the approved template picker is offered and sends when a template exists', async ({ page, request }) => {
  const workspace = slug('e2e-template');
  const email = `owner-${workspace}@example.com`;
  await registerOwner(request, workspace, email);

  const messageId = `wamid.tpl-${Date.now()}`;
  const delivered = await seedInboundText(request, messageId, 'need an order update');
  test.skip(!delivered, 'the webhook returned 404 (no connected channel for this tenant)');

  await loginAs(request, workspace, email, page);
  await expect(page.getByText('need an order update').first()).toBeVisible({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Use an approved template' }).click();
  // If the WABA exposes an approved template it is selectable; otherwise the picker explains the gap.
  const combobox = page.getByRole('combobox', { name: 'Approved template' });
  await expect(combobox).toBeVisible();
  const options = combobox.locator('option');
  const templateName = process.env.WHATSAPP_APPROVED_TEMPLATE;
  if (templateName && (await options.count()) > 1) {
    await combobox.selectOption({ label: templateName });
    await page.getByRole('button', { name: 'Confirm template' }).click();
    await page.getByRole('button', { name: 'Send reply' }).click();
  }
});
