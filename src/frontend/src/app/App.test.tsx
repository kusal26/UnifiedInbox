import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { AuthProvider } from '../auth/AuthProvider';
import { App } from './App';

afterEach(cleanup);

describe('App', () => {
  it('navigates to Channels by keyboard and marks the active workspace link', async () => {
    window.history.pushState({}, '', '/');
    render(<AuthProvider login={async () => ({ accessToken: 'test-token' })}><App /></AuthProvider>);

    await userEvent.type(screen.getByLabelText('Workspace slug'), 'acme');
    await userEvent.type(screen.getByLabelText('Email'), 'agent@acme.test');
    await userEvent.type(screen.getByLabelText('Password'), 'demo');
    await userEvent.click(screen.getByRole('button', { name: 'Open inbox' }));

    const channels = await screen.findByRole('link', { name: 'Channels' });
    channels.focus();
    expect(channels).toHaveFocus();
    await userEvent.keyboard('{Enter}');

    expect(screen.getByRole('heading', { name: 'Channels' })).toBeVisible();
    expect(channels).toHaveAttribute('aria-current', 'page');

    await userEvent.click(screen.getByRole('button', { name: 'Log out' }));
    expect(screen.getByRole('heading', { name: 'Open your workspace' })).toBeVisible();
  });

  it('shows demo-local workspace views with an interactive team invite', async () => {
    window.history.pushState({}, '', '/');
    render(<AuthProvider login={async () => ({ accessToken: 'test-token' })}><App /></AuthProvider>);

    await userEvent.type(screen.getByLabelText('Workspace slug'), 'acme');
    await userEvent.type(screen.getByLabelText('Email'), 'agent@acme.test');
    await userEvent.type(screen.getByLabelText('Password'), 'demo');
    await userEvent.click(screen.getByRole('button', { name: 'Open inbox' }));

    await userEvent.click(await screen.findByRole('link', { name: 'Team' }));
    expect(screen.getByText('Demo-local data')).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Invite teammate' }));
    expect(screen.getByText('Invitation prepared for demo')).toBeVisible();
  });
});
