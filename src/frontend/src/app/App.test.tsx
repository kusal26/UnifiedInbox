import { renderToStaticMarkup } from 'react-dom/server';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AppShell } from './AppShell';

describe('App', () => {
  it('marks Channels as the active workspace link', () => {
    const markup = renderToStaticMarkup(<MemoryRouter initialEntries={['/channels']}><AppShell><h1>Channels</h1></AppShell></MemoryRouter>);

    expect(markup).toContain('<h1>Channels</h1>');
    expect(markup).toMatch(/aria-current="page"[^>]*href="\/channels"/);
  });
});
