import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider } from './AuthProvider';
import { LoginPage } from './LoginPage';

describe('LoginPage', () => {
  afterEach(cleanup);

  it('submits the workspace credentials through auth', async () => {
    const login = vi.fn().mockResolvedValue({ accessToken: 'test-token' });
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <AuthProvider login={login}>
          <LoginPage />
        </AuthProvider>
      </MemoryRouter>,
    );

    await user.type(screen.getByLabelText('Workspace slug'), 'acme');
    await user.type(screen.getByLabelText('Email'), 'agent@acme.test');
    await user.type(screen.getByLabelText('Password'), 'demo');
    await user.click(screen.getByRole('button', { name: 'Open inbox' }));

    expect(login).toHaveBeenCalledWith({
      tenantSlug: 'acme',
      email: 'agent@acme.test',
      password: 'demo',
    });
  });

  it('keeps credentials and focuses the server error when login is rejected', async () => {
    const login = vi.fn().mockRejectedValue(new Error('Incorrect workspace credentials'));
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <AuthProvider login={login}>
          <LoginPage />
        </AuthProvider>
      </MemoryRouter>,
    );

    await user.type(screen.getByLabelText('Workspace slug'), 'acme');
    await user.type(screen.getByLabelText('Email'), 'agent@acme.test');
    await user.type(screen.getByLabelText('Password'), 'demo');
    await user.click(screen.getByRole('button', { name: 'Open inbox' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Incorrect workspace credentials');
    expect(alert).toHaveFocus();
    expect(screen.getByLabelText('Workspace slug')).toHaveValue('acme');
    expect(screen.getByLabelText('Email')).toHaveValue('agent@acme.test');
    expect(screen.getByLabelText('Password')).toHaveValue('demo');
  });
});
