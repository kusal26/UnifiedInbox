import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { Icon } from './Icon';

describe('Icon', () => {
  it('exposes a labelled icon to assistive technology', () => {
    const markup = renderToStaticMarkup(<Icon name="bell" label="Notifications" />);

    expect(markup).toContain('role="img"');
    expect(markup).toContain('aria-label="Notifications"');
    expect(markup).not.toContain('aria-hidden="true"');
  });
});
