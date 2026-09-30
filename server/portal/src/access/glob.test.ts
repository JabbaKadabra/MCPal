import { describe, expect, it } from 'vitest';
import { globMatches, isValidToolPattern, parsePatterns, previewGrant } from './glob';

describe('globMatches', () => {
  it.each([
    ['*', 'anything', true],
    ['list_*', 'list_tables', true],
    ['list_*', 'get_tables', false],
    ['get_?', 'get_a', true],
    ['get_?', 'get_ab', false],
    ['a.b', 'a.b', true],
    ['a.b', 'axb', false],
    ['*_page', 'get_page', true],
    ['*a*b*', 'xxaxxbxx', true],
    ['*a*b*', 'xxbxxaxx', false],
    ['', '', true],
    ['**', 'x', true],
  ])('%s matches %s: %s', (pattern, name, expected) => {
    expect(globMatches(pattern, name, false)).toBe(expected);
  });

  it('is case-sensitive unless told otherwise', () => {
    expect(globMatches('Query', 'query', false)).toBe(false);
    expect(globMatches('HR*', 'hr-portal', true)).toBe(true);
  });
});

describe('isValidToolPattern', () => {
  it.each([
    ['*', true],
    ['get-page_1.x?', true],
    ['', false],
    ['a b', false],
    ['a/b', false],
    ['[a]', false],
  ])('%s is valid: %s', (pattern, expected) => {
    expect(isValidToolPattern(pattern)).toBe(expected);
  });
});

describe('parsePatterns', () => {
  it('splits, trims and removes blanks and duplicates', () => {
    expect(parsePatterns(' list_*, get_? ,, list_* ')).toEqual(['list_*', 'get_?']);
  });
});

describe('previewGrant', () => {
  const online = [
    { server: 'HR', tool: 'salaries' },
    { server: 'hr', tool: 'list_staff' },
    { server: 'wiki', tool: 'search' },
  ];

  it('matches servers without case and tools with case', () => {
    expect(previewGrant('hr', ['*'], online).map((t) => t.tool)).toEqual(['salaries', 'list_staff']);
    expect(previewGrant('*', ['Search'], online)).toEqual([]);
    expect(previewGrant('wiki', ['search'], online)).toEqual([{ server: 'wiki', tool: 'search' }]);
  });
});
