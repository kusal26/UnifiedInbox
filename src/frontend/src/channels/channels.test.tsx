import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChannelRepairPage } from './ChannelRepairPage';
import { extractSignupSession, type EmbeddedSignupSession } from './EmbeddedSignupButton';
import { AuthProvider } from '../auth/AuthProvider';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const me = { id: 'u-owner', tenantId: 't1', email: 'o@x.test', displayName: 'Owner', role: 0, workspaceName: 'Acme' };

const attempt = {
  attemptId: 'a1', state: 'state-1', nonce: 'nonce-1', metaAppId: 'app-1', configurationId: 'config-1',
  graphVersion: 'v23.0', embeddedSignupVersion: 'v4', expiresAt: '2026-01-01T00:10:00Z',
};

describe('extractSignupSession', () => {
  it('accepts a WhatsApp Embedded Signup payload from the Meta origin', () => {
    const session = extractSignupSession(new MessageEvent('message', {
      origin: 'https://www.facebook.com',
      data: { type: 'WA_EMBEDDED_SIGNUP', data: { code: 'code-1', phone_number_id: 'phone-1', business_id: 'waba-1' } },
    }));
    expect(session).toEqual({ code: 'code-1', phoneNumberId: 'phone-1', businessId: 'waba-1' });
  });

  it('accepts business.facebook.com and camelCase keys', () => {
    const session = extractSignupSession(new MessageEvent('message', {
      origin: 'https://business.facebook.com',
      data: { code: 'code-2', phoneNumberId: 'phone-2', businessId: 'waba-2' },
    }));
    expect(session).toEqual({ code: 'code-2', phoneNumberId: 'phone-2', businessId: 'waba-2' });
  });

  it('rejects payloads from any other origin', () => {
    const session = extractSignupSession(new MessageEvent('message', {
      origin: 'https://attacker.example',
      data: { type: 'WA_EMBEDDED_SIGNUP', data: { code: 'code-3', phone_number_id: 'phone-3', business_id: 'waba-3' } },
    }));
    expect(session).toBeNull();
  });

  it('rejects a payload missing the identifiers', () => {
    const session = extractSignupSession(new MessageEvent('message', {
      origin: 'https://www.facebook.com',
      data: { code: 'code-4' },
    }));
    expect(session).toBeNull();
  });
});

describe('ChannelRepairPage', () => {
  function renderRepair(fetchImpl: (url: unknown) => Promise<Response>) {
    globalThis.fetch = vi.fn(fetchImpl) as typeof fetch;
    const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
    render(<QueryClientProvider client={client}><AuthProvider initialToken="token-1" login={vi.fn()}><MemoryRouter initialEntries={['/channels/ch-1/repair']}>
      <Routes><Route path="/channels/:channelId/repair" element={<ChannelRepairPage />} /></Routes>
    </MemoryRouter></AuthProvider></QueryClientProvider>);
  }

  it('starts a reauthorization attempt, completes from the Meta payload, and navigates back', async () => {
    let completed = false;
    const fetchImpl = async (url: unknown) => {
      const key = String(url);
      if (key === '/api/v1/auth/me') return new Response(JSON.stringify(me));
      if (key === '/api/v1/channels') return new Response(JSON.stringify([{ id: 'ch-1', displayName: 'Sales', platform: 'whatsapp', externalAccountId: 'phone-1', isHealthy: false, isEnabled: true, status: 'connected' }]));
      if (key === '/api/v1/channels/ch-1/reauthorize') return new Response(JSON.stringify(attempt));
      if (key === '/api/v1/channels/connect/complete') {
        completed = true;
        return new Response(JSON.stringify({ id: 'ch-1', displayName: 'Sales', platform: 'whatsapp', externalAccountId: 'phone-1', isHealthy: true, isEnabled: true, status: 'connected' }));
      }
      throw new Error(`unexpected ${url}`);
    };
    renderRepair(fetchImpl);

    await userEvent.click(await screen.findByRole('button', { name: 'Start repair' }));
    expect(await screen.findByRole('button', { name: /Continue in the Meta popup/ })).toBeVisible();

    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://www.facebook.com',
      data: { type: 'WA_EMBEDDED_SIGNUP', data: { code: 'code-1', phone_number_id: 'phone-1', business_id: 'waba-1' } },
    }));

    await vi.waitFor(() => expect(completed).toBe(true));
  });

  it('hides the repair controls from agents', async () => {
    const agent = { ...me, role: 2 };
    renderRepair(async (url: unknown) => {
      const key = String(url);
      if (key === '/api/v1/auth/me') return new Response(JSON.stringify(agent));
      if (key === '/api/v1/channels') return new Response(JSON.stringify([]));
      throw new Error(`unexpected ${url}`);
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(/administrator role/);
  });

  it('invokes onSession exactly once with the valid session shape', async () => {
    const onSession = vi.fn((session: EmbeddedSignupSession) => { void session; });
    const { EmbeddedSignupButton } = await import('./EmbeddedSignupButton');
    render(<MemoryRouter><EmbeddedSignupButton attempt={attempt} onSession={onSession} /></MemoryRouter>);
    await userEvent.click(screen.getByRole('button', { name: /Continue in the Meta popup/ }));

    window.dispatchEvent(new MessageEvent('message', { origin: 'https://www.facebook.com', data: { code: 'code-9', phone_number_id: 'phone-9', business_id: 'waba-9' } }));
    window.dispatchEvent(new MessageEvent('message', { origin: 'https://evil.example', data: { code: 'code-9', phone_number_id: 'phone-9', business_id: 'waba-9' } }));

    await vi.waitFor(() => expect(onSession).toHaveBeenCalledTimes(1));
    expect(onSession).toHaveBeenCalledWith({ code: 'code-9', phoneNumberId: 'phone-9', businessId: 'waba-9' });
  });
});
