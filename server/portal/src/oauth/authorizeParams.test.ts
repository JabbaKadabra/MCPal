import { describe, expect, it } from 'vitest';
import { buildAuthorizeBody, buildDenyUrl, parseAuthorizeParams } from './authorizeParams';

const search =
  '?client_id=abc&redirect_uri=https%3A%2F%2Fclaude.ai%2Fapi%2Fmcp%2Fauth_callback&response_type=code' +
  '&code_challenge=chal&code_challenge_method=S256&state=st&resource=https%3A%2F%2Fmcpal.example.com%2Fmcp';

describe('parseAuthorizeParams', () => {
  it('reads all parameters of a Claude authorization link', () => {
    expect(parseAuthorizeParams(search)).toEqual({
      client_id: 'abc',
      redirect_uri: 'https://claude.ai/api/mcp/auth_callback',
      response_type: 'code',
      code_challenge: 'chal',
      code_challenge_method: 'S256',
      state: 'st',
      scope: '',
      resource: 'https://mcpal.example.com/mcp',
    });
  });

  it('returns null without client_id or redirect_uri', () => {
    expect(parseAuthorizeParams('?redirect_uri=https%3A%2F%2Fx.example')).toBeNull();
    expect(parseAuthorizeParams('?client_id=abc')).toBeNull();
    expect(parseAuthorizeParams('')).toBeNull();
  });
});

describe('buildAuthorizeBody', () => {
  it('carries the request parameters and no credential', () => {
    const params = parseAuthorizeParams(search);
    if (params === null) throw new Error('parse failed');

    const body = buildAuthorizeBody(params);

    expect(body.client_id).toBe('abc');
    expect(body.code_challenge).toBe('chal');
    expect(body).not.toHaveProperty('api_key');
    expect(body).not.toHaveProperty('use_session');
  });

  it('omits empty optional parameters', () => {
    const params = parseAuthorizeParams('?client_id=a&redirect_uri=https%3A%2F%2Fx.example');
    if (params === null) throw new Error('parse failed');

    const body = buildAuthorizeBody(params);

    expect(body.resource).toBeUndefined();
    expect(body.state).toBeUndefined();
    expect(body.scope).toBeUndefined();
  });
});

describe('buildDenyUrl', () => {
  it('redirects with access_denied and the state', () => {
    const params = parseAuthorizeParams(search);
    if (params === null) throw new Error('parse failed');

    const url = new URL(buildDenyUrl(params));

    expect(url.origin + url.pathname).toBe('https://claude.ai/api/mcp/auth_callback');
    expect(url.searchParams.get('error')).toBe('access_denied');
    expect(url.searchParams.get('state')).toBe('st');
  });
});
