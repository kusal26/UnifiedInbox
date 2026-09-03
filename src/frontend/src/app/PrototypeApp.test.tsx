import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { AuthProvider } from '../auth/AuthProvider';
import { App } from './App';

afterEach(cleanup);

describe('App', () => {
  it('uses routed React screens instead of embedding the prototype document', async () => {
    render(<AuthProvider><App /></AuthProvider>);
    expect(await screen.findByRole('heading', { name: /open your workspace/i })).toBeInTheDocument();
    expect(screen.queryByTitle('Unified Inbox')).not.toBeInTheDocument();
  });
});
