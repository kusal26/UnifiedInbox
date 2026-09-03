import { describe, expect, it, vi } from 'vitest';
import { createAuthApi } from './auth';
import { createAdminApi } from './admin';
import { createAttachmentsApi } from './attachments';

function jsonResponse(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('createAuthApi', () => {
  it('registers a workspace and normalizes the current user role', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ message: 'created' }))
      .mockResolvedValueOnce(jsonResponse({ id: 'u1', tenantId: 't1', email: 'a@x.test', displayName: 'A', role: 0, workspaceName: 'Acme' }));
    const api = createAuthApi(() => null, fetcher);

    await api.register({ workspaceName: 'Acme', workspaceSlug: 'acme', displayName: 'A', email: 'a@x.test', password: 'long-enough-password' });
    expect(fetcher).toHaveBeenCalledWith('/api/v1/auth/register', expect.objectContaining({ method: 'POST' }));

    await expect(api.me()).resolves.toMatchObject({ role: 'Owner', workspaceName: 'Acme' });
  });

  it('drives the verification and password-reset flows', async () => {
    const fetcher = vi.fn().mockImplementation(() => Promise.resolve(jsonResponse({ verified: true })));
    const api = createAuthApi(() => null, fetcher);

    await api.verifyEmail('tok');
    await api.forgotPassword('a@x.test');
    expect(fetcher).toHaveBeenCalledWith('/api/v1/auth/verify-email', expect.objectContaining({ method: 'POST' }));
    expect(fetcher).toHaveBeenCalledWith('/api/v1/auth/forgot-password', expect.objectContaining({ method: 'POST' }));
  });
});

describe('createAdminApi', () => {
  it('invites members and reads notifications with auth headers', async () => {
    const fetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ id: 'i1', email: 'b@x.test', role: 2, expiresAt: '2026-01-01T00:00:00Z', createdAt: '2026-01-01T00:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse([]));
    const api = createAdminApi(() => 'token-1', fetcher);

    await expect(api.invite('b@x.test', 'Agent')).resolves.toMatchObject({ email: 'b@x.test', role: 'Agent' });
    await api.notifications(true);
    expect(fetcher).toHaveBeenLastCalledWith('/api/v1/notifications?unreadOnly=true', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: 'Bearer token-1' }),
    }));
  });

  it('requests overview metrics for the selected window', async () => {
    const fetcher = vi.fn().mockResolvedValue(jsonResponse({ days: 7, conversationsOpened: 3 }));
    await createAdminApi(() => 't', fetcher).metrics(7);
    expect(fetcher).toHaveBeenCalledWith('/api/v1/metrics/overview?days=7', expect.anything());
  });
});

describe('createAttachmentsApi', () => {
  it('uploads bytes straight to the presigned URL then completes', async () => {
    const staged = { id: 'a1', fileName: 'photo.jpg', contentType: 'image/jpeg', size: 5, expiresAt: '2026-01-01T00:00:00Z', objectKey: 'k', uploadUrl: 'https://storage.test/k?put=1' };
    const apiFetcher = vi.fn()
      .mockResolvedValueOnce(jsonResponse(staged))
      .mockResolvedValueOnce(jsonResponse({ completed: true }));
    const putFetcher = vi.fn().mockResolvedValue(new Response(null, { status: 200 }));
    const api = createAttachmentsApi(() => 't', apiFetcher);

    const file = new File(['hello'], 'photo.jpg', { type: 'image/jpeg' });
    await expect(api.upload(file, putFetcher)).resolves.toBe('a1');
    expect(putFetcher).toHaveBeenCalledWith('https://storage.test/k?put=1', expect.objectContaining({ method: 'PUT', body: file }));
    expect(apiFetcher).toHaveBeenLastCalledWith('/api/v1/attachments/a1/complete', expect.objectContaining({ method: 'POST' }));
  });
});
