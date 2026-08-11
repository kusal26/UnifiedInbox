import '@testing-library/jest-dom/vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App', () => {
  it('navigates to Channels by keyboard and marks the active workspace link', async () => {
    window.history.pushState({}, '', '/');
    render(<App />);

    const channels = screen.getByRole('link', { name: 'Channels' });
    channels.focus();
    expect(channels).toHaveFocus();
    await userEvent.keyboard('{Enter}');

    expect(screen.getByRole('heading', { name: 'Channels' })).toBeVisible();
    expect(channels).toHaveAttribute('aria-current', 'page');
  });
});
