// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { App } from './App';

describe('App', () => {
  it('navigates to Channels and marks the active workspace link', () => {
    render(<App />);

    fireEvent.click(screen.getByRole('link', { name: 'Channels' }));

    expect(screen.getByRole('heading', { name: 'Channels' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Channels' })).toHaveAttribute('aria-current', 'page');
  });
});
