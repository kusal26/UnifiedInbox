import '@testing-library/jest-dom/vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import prototypeDocument from '../../../../preview.html?raw';
import { App } from './App';

afterEach(cleanup);

describe('App', () => {
  it('renders the approved prototype in an isolated full-page document', () => {
    render(<App />);
    const frame = screen.getByTitle('Unified Inbox');
    expect(frame).toHaveClass('prototype-frame');
    expect(frame).toHaveAttribute('srcdoc', prototypeDocument);
  });

  it('ships the primary mocked workspace views and interactions', () => {
    for (const label of ['Shared Inbox', 'Overview', 'Channels', 'Team', 'Canned Responses', 'Audit Log', 'Settings']) {
      expect(prototypeDocument).toContain(label);
    }
    expect(prototypeDocument).toContain('simulateIncomingMessage');
  });
});
