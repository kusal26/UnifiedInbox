import { describe, expect, it } from 'vitest';
import { normalizeActivity } from './activity';

describe('normalizeActivity', () => {
  it('normalizes the paginated activity API response', () => {
    expect(normalizeActivity({ items: [{ body: 'hello' }], nextCursor: null })).toEqual([{ body: 'hello' }]);
  });
});
