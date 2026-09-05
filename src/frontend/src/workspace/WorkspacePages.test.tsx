import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ChannelsPage, NotificationsPage, OverviewPage, TeamPage } from './WorkspacePages';
import { AuthProvider } from '../auth/AuthProvider';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const me = { id: 'u-owner', tenantId: 't1', email: 'o@x.test', displayName: 'Owner', role: 0, workspaceName: 'Acme' };

function stubFetch(routes: Record<string, unknown>) {
  globalThis.fetch = vi.fn(async (url: unknown) => {
    const key = String(url).split('?')[0];
    if (!(key in routes)) throw new Error(`unexpected request ${url}`);
    return new Response(JSON.stringify(routes[key]), { headers: { 'Content-Type': 'application/json' } });
  }) as typeof fetch;
}

function renderWorkspace(ui: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><AuthProvider initialToken="token-1" login={vi.fn()}><MemoryRouter>{ui}</MemoryRouter></AuthProvider></QueryClientProvider>);
}

describe('OverviewPage', () => {
  it('renders live metrics for the selected window', async () => {
    stubFetch({ '/api/v1/metrics/overview': { days: 7, conversationsOpened: 4, openConversations: 2, messagesInbound: 9, messagesOutbound: 7, notesCreated: 1 } });
    renderWorkspace(<OverviewPage />);

    await userEvent.selectOptions(screen.getByLabelText('Metrics window'), '7');
    expect(await screen.findByText('4')).toBeVisible();
    expect(screen.getByText('Inbound messages')).toBeVisible();
  });
});

describe('TeamPage', () => {
  it('invites a member and lists pending invitations', async () => {
    stubFetch({
      '/api/v1/auth/me': me,
      '/api/v1/users': [{ id: 'u-owner', displayName: 'Owner', email: 'o@x.test', role: 0, isActive: true }],
      '/api/v1/invitations': [{ id: 'i1', email: 'b@x.test', role: 2, expiresAt: '2026-01-01T00:00:00Z', createdAt: '2026-01-01T00:00:00Z' }],
    });
    renderWorkspace(<TeamPage />);

    await userEvent.type(await screen.findByLabelText('Invite email'), 'c@x.test');
    await userEvent.click(screen.getByRole('button', { name: 'Send invitation' }));
    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalledWith('/api/v1/invitations', expect.objectContaining({ method: 'POST' })));
    expect(await screen.findByText(/b@x.test/)).toBeVisible();
  });
});

describe('ChannelsPage', () => {
  it('starts an Embedded Signup attempt and completes the connection from the Meta postMessage', async () => {
    let calls = 0;
    globalThis.fetch = vi.fn(async (url: unknown) => {
      calls += 1;
      const key = String(url);
      if (key === '/api/v1/auth/me') return new Response(JSON.stringify(me));
      if (key === '/api/v1/channels') return new Response(JSON.stringify([]));
      if (key === '/api/v1/channels/connect/attempt') return new Response(JSON.stringify({
        attemptId: 'a1', state: 'state-1', nonce: 'nonce-1', metaAppId: 'app-1', configurationId: 'config-1',
        graphVersion: 'v23.0', embeddedSignupVersion: 'v4', expiresAt: '2026-01-01T00:10:00Z',
      }));
      if (key === '/api/v1/channels/connect/complete') return new Response(JSON.stringify({ id: 'ch-1', displayName: 'Sales', platform: 'whatsapp', externalAccountId: 'phone-1', isHealthy: true, isEnabled: true, status: 'connected' }));
      throw new Error(`unexpected ${key}`);
    }) as typeof fetch;
    renderWorkspace(<ChannelsPage />);

    await userEvent.type(await screen.findByLabelText('Channel display name'), 'Sales');
    await userEvent.click(screen.getByRole('button', { name: 'Start Embedded Signup' }));

    expect(await screen.findByText(/Complete Meta Embedded Signup in the popup/)).toBeVisible();
    expect(screen.queryByLabelText('Authorization code')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Phone number ID')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Business ID')).not.toBeInTheDocument();

    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://www.facebook.com',
      data: { type: 'WA_EMBEDDED_SIGNUP', data: { code: 'code-1', phone_number_id: 'phone-1', business_id: 'waba-1' } },
    }));

    expect(await screen.findByText(/Connected Sales/)).toBeVisible();
    expect(calls).toBeGreaterThan(0);
  });

  it('ignores postMessages from origins other than Meta', async () => {
    globalThis.fetch = vi.fn(async (url: unknown) => {
      const key = String(url);
      if (key === '/api/v1/auth/me') return new Response(JSON.stringify(me));
      if (key === '/api/v1/channels') return new Response(JSON.stringify([]));
      if (key === '/api/v1/channels/connect/attempt') return new Response(JSON.stringify({
        attemptId: 'a1', state: 'state-1', nonce: 'nonce-1', metaAppId: 'app-1', configurationId: 'config-1',
        graphVersion: 'v23.0', embeddedSignupVersion: 'v4', expiresAt: '2026-01-01T00:10:00Z',
      }));
      throw new Error(`unexpected ${url}`);
    }) as typeof fetch;
    renderWorkspace(<ChannelsPage />);

    await userEvent.type(await screen.findByLabelText('Channel display name'), 'Sales');
    await userEvent.click(screen.getByRole('button', { name: 'Start Embedded Signup' }));
    await screen.findByText(/Complete Meta Embedded Signup in the popup/);

    window.dispatchEvent(new MessageEvent('message', {
      origin: 'https://evil.example',
      data: { type: 'WA_EMBEDDED_SIGNUP', data: { code: 'code-1', phone_number_id: 'phone-1', business_id: 'waba-1' } },
    }));

    expect(screen.queryByText(/Connected Sales/)).not.toBeInTheDocument();
    expect(screen.getByText(/Complete Meta Embedded Signup in the popup/)).toBeVisible();
  });
});

describe('NotificationsPage', () => {
  it('marks notifications read and toggles preferences', async () => {
    const read: string[] = [];
    globalThis.fetch = vi.fn(async (url: unknown, init?: RequestInit) => {
      const key = String(url).split('?')[0];
      if (key === '/api/v1/auth/me') return new Response(JSON.stringify(me));
      if (key === '/api/v1/notifications' && (!init?.method || init.method === 'GET')) return new Response(JSON.stringify([{ id: 'n1', type: 'message.failed', text: 'Send failed', isRead: false, createdAt: '2026-01-01T00:00:00Z' }]));
      if (key === '/api/v1/notifications/n1/read') { read.push('n1'); return new Response(JSON.stringify({})); }
      if (key === '/api/v1/notifications/read-all') return new Response(JSON.stringify({}));
      if (key === '/api/v1/notification-preferences') return new Response(JSON.stringify([]));
      throw new Error(`unexpected ${url}`);
    }) as typeof fetch;
    renderWorkspace(<NotificationsPage />);

    await userEvent.click(await screen.findByRole('button', { name: 'Mark read' }));
    await waitFor(() => expect(read).toEqual(['n1']));
    expect(await screen.findByText('message.received')).toBeVisible();
  });
});

describe('role gating', () => {
  it('hides owner-only navigation from agents', async () => {
    const { AppShell } = await import('../app/AppShell');
    stubFetch({
      '/api/v1/auth/me': { ...me, role: 2 },
      '/api/v1/notifications': [],
    });
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(<QueryClientProvider client={client}><AuthProvider initialToken="token-1" login={vi.fn()}><MemoryRouter><AppShell onLogout={() => undefined}><p>content</p></AppShell></MemoryRouter></AuthProvider></QueryClientProvider>);

    await screen.findByText('Acme');
    expect(screen.queryByText('Audit Log')).not.toBeInTheDocument();
    expect(screen.queryByText('Team')).not.toBeInTheDocument();
    expect(screen.getByText('Shared Inbox')).toBeVisible();
    expect(within(screen.getByLabelText('Workspace navigation')).getByText('Notifications')).toBeVisible();
  });
});
