import { describe, expect, it, vi } from 'vitest';
import { ApiError, request } from './client';

describe('request', () => {
  it('throws ApiError with the JSON error status and message', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ error: 'Access denied' }), { status: 403 }));

    await expect(request(fetcher, '/api/v1/conversations')).rejects.toEqual(
      expect.objectContaining<ApiError>({ name: 'ApiError', status: 403, message: 'Access denied' }),
    );
  });

  it('exposes the stable problem code and detail for branching UI', async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(JSON.stringify({ code: 'messaging_window_closed', detail: 'The window is closed.', title: 'Unprocessable Entity' }), { status: 422 }));

    await expect(request(fetcher, '/api/v1/conversations/c1/messages')).rejects.toEqual(
      expect.objectContaining<ApiError>({ name: 'ApiError', status: 422, code: 'messaging_window_closed', message: 'The window is closed.' }),
    );
  });
});
