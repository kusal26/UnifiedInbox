import { describe, expect, it, vi } from 'vitest';
import { createInboxApi } from './inbox';

describe('createInboxApi', () => {
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
});
