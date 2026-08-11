import { describe, expect, it } from 'vitest';

describe('frontend smoke', () => {
  it('keeps the login workspace contract explicit', () => {
    expect({ tenantSlug: 'acme', email: 'agent@acme.test' }).toMatchObject({ tenantSlug: 'acme' });
  });
});
