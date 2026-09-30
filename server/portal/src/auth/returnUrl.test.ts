import { describe, expect, it } from 'vitest';
import { loginUrlFor, safeReturnUrl } from './returnUrl';

describe('safeReturnUrl', () => {
  it('accepts paths of this app, with query', () => {
    expect(safeReturnUrl('/oauth/authorize?client_id=a&state=b')).toBe('/oauth/authorize?client_id=a&state=b');
    expect(safeReturnUrl('/keys')).toBe('/keys');
  });

  it.each([null, '', 'keys', 'https://evil.example/x', '//evil.example/x', '/\\evil.example', 'javascript:alert(1)'])(
    'rejects %s',
    (value) => {
      expect(safeReturnUrl(value)).toBeNull();
    },
  );
});

describe('loginUrlFor', () => {
  it('encodes the target so its query survives', () => {
    const url = loginUrlFor('/oauth/authorize?client_id=a&state=b');

    expect(new URL(url, 'http://x').searchParams.get('returnUrl')).toBe('/oauth/authorize?client_id=a&state=b');
  });
});
