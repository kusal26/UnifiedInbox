import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AcceptInvitationPage } from './AcceptInvitationPage';
import { ForgotPasswordPage, RegisterPage, ResetPasswordPage, VerifyEmailPage } from './AuthPages';
import { AuthProvider } from './AuthProvider';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function stubFetch(handler: (url: string, init?: RequestInit) => unknown) {
  globalThis.fetch = vi.fn(async (url: unknown, init?: RequestInit) => new Response(JSON.stringify(handler(String(url), init)))) as typeof fetch;
}

function renderAuth(ui: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(<QueryClientProvider client={client}><AuthProvider initialToken={null} login={vi.fn()}><MemoryRouter>{ui}</MemoryRouter></AuthProvider></QueryClientProvider>);
}

describe('RegisterPage', () => {
  it('creates a workspace and continues to verification', async () => {
    stubFetch((url) => {
      expect(url).toBe('/api/v1/auth/register');
      return { message: 'created' };
    });
    renderAuth(<RegisterPage />);

    await userEvent.type(screen.getByLabelText(/Workspace name/), 'Acme');
    await userEvent.type(screen.getByLabelText(/Workspace slug/), 'acme');
    await userEvent.type(screen.getByLabelText(/Your name/), 'Ada');
    await userEvent.type(screen.getByLabelText(/^Email/), 'ada@acme.test');
    await userEvent.type(screen.getByLabelText(/^Password/), 'long-enough-password');
    await userEvent.click(screen.getByRole('button', { name: 'Create workspace' }));

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
  });

  it('requires a 12-character password', async () => {
    renderAuth(<RegisterPage />);
    expect(screen.getByLabelText(/^Password/)).toHaveAttribute('minLength', '12');
  });
});

describe('VerifyEmailPage', () => {
  it('verifies with a token and links to sign in', async () => {
    stubFetch(() => ({ verified: true }));
    renderAuth(<VerifyEmailPage />);

    await userEvent.type(screen.getByLabelText('Verification token'), 'tok-1');
    await userEvent.click(screen.getByRole('button', { name: 'Verify email' }));

    expect(await screen.findByText(/Your email is verified/)).toBeVisible();
  });
});

describe('ForgotPasswordPage', () => {
  it('confirms without disclosing account existence', async () => {
    stubFetch(() => ({ message: 'sent' }));
    renderAuth(<ForgotPasswordPage />);

    await userEvent.type(screen.getByLabelText(/^Email/), 'ada@acme.test');
    await userEvent.click(screen.getByRole('button', { name: 'Send reset email' }));

    expect(await screen.findByText(/If the account exists/)).toBeVisible();
  });
});

describe('ResetPasswordPage', () => {
  it('resets and links to sign in', async () => {
    stubFetch(() => ({ reset: true }));
    renderAuth(<ResetPasswordPage />);

    await userEvent.type(screen.getByLabelText('Reset token'), 'tok-1');
    await userEvent.type(screen.getByLabelText(/New password/), 'another-long-password');
    await userEvent.click(screen.getByRole('button', { name: 'Reset password' }));

    expect(await screen.findByText(/Your password was reset/)).toBeVisible();
  });
});

describe('AcceptInvitationPage', () => {
  it('joins the workspace with an invitation token', async () => {
    stubFetch((url, init) => {
      expect(url).toBe('/api/v1/invitations/accept');
      const body = JSON.parse(String(init?.body));
      expect(body.displayName).toBe('Bob');
      return { accepted: true };
    });
    renderAuth(<AcceptInvitationPage />);

    await userEvent.type(screen.getByLabelText('Invitation token'), 'invite-1');
    await userEvent.type(screen.getByLabelText(/Your name/), 'Bob');
    await userEvent.type(screen.getByLabelText(/^Password/), 'long-enough-password');
    await userEvent.click(screen.getByRole('button', { name: 'Join workspace' }));

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
  });
});

beforeEach(() => { window.history.replaceState({}, '', '/'); });
