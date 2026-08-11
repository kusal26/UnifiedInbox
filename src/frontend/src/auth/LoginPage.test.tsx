import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { AuthProvider } from './AuthProvider';
import { LoginPage } from './LoginPage';

describe('LoginPage', () => {
  it('submits the workspace credentials through auth', async () => {
    const login = vi.fn().mockResolvedValue(undefined);
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
});
