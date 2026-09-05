import { describe, expect, it, vi } from 'vitest';
import { createInboxApi } from './inbox';

describe('createInboxApi', () => {
  it('includes credentials so login can receive the refresh cookie', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ accessToken: 'jwt' })));

    await createInboxApi(() => null, fetcher).login({ tenantSlug: 'acme', email: 'owner@acme.test', password: 'secret' });

    expect(fetcher).toHaveBeenCalledWith('/api/v1/auth/login', expect.objectContaining({ credentials: 'include' }));
  });

  it('sends a message with authentication and an idempotency key', async () => {
    const fetcher = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ id: 'message-1' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );

    await createInboxApi(() => 'token-1', fetcher).sendMessage('c1', 'Hello', 'key-1');

    expect(fetcher).toHaveBeenCalledWith('/api/v1/conversations/c1/messages', {
      method: 'POST',
      headers: expect.objectContaining({
        Authorization: 'Bearer token-1',
        'Idempotency-Key': 'key-1',
      }),
      body: JSON.stringify({ body: 'Hello' }),
    });
  });

  it('converts numeric conversation statuses at the API boundary', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      items: [
        { id: 'c1', contactName: 'Jamie', platform: 'whatsapp', preview: 'Hi', status: 1, unread: true, updatedAt: '2026-01-01T00:00:00Z' },
      ],
      nextCursor: 'cursor-1',
    })));

    await expect(createInboxApi(() => 'token-1', fetcher).listConversations()).resolves.toEqual({
      items: [expect.objectContaining({ id: 'c1', status: 'Pending' })],
      nextCursor: 'cursor-1',
    });
  });

  it('converts numeric activity enums and maps senderUserId to authorId', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({
      items: [{ id: 'm1', conversationId: 'c1', kind: 0, body: 'Hello', createdAt: '2026-01-01T00:00:00Z', sequence: 2, senderUserId: 'user-1', status: 2 }],
      nextCursor: null,
    })));

    await expect(createInboxApi(() => 'token-1', fetcher).getActivity('c1')).resolves.toEqual({
      items: [expect.objectContaining({ kind: 'Message', authorId: 'user-1', status: 'Sent' })],
      nextCursor: null,
    });
  });

  it('sends an approved template with its components as a nested request', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'm1', conversationId: 'c1', kind: 0, body: '', createdAt: '', sequence: 1 }), { status: 202 }));

    await createInboxApi(() => 'token-1', fetcher).sendMessage('c1', '', 'key-tpl', {
      template: { name: 'order_shipping', language: 'en_US', components: [{ type: 'BODY', parameters: [{ type: 'text', text: 'order 42' }] }] },
    });

    expect(fetcher).toHaveBeenCalledWith('/api/v1/conversations/c1/messages', expect.objectContaining({
      body: JSON.stringify({
        body: '',
        template: { name: 'order_shipping', language: 'en_US', components: [{ type: 'BODY', parameters: [{ type: 'text', text: 'order 42' }] }] },
      }),
    }));
  });

  it('sends numeric statuses expected by the ASP.NET stub', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ id: 'c1', status: 0 })));

    await createInboxApi(() => 'token-1', fetcher).setStatus('c1', 'Open');

    expect(fetcher).toHaveBeenCalledWith('/api/v1/conversations/c1/status', expect.objectContaining({
      method: 'PATCH',
      body: JSON.stringify({ status: 0 }),
    }));
  });
});
