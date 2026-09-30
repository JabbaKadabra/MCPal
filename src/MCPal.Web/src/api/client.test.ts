import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError, resetCsrfToken } from './client';
import { mockFetch } from '../test/fetchMock';

describe('api client', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends the anti-forgery header on mutating calls and fetches the token only once', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 'tok-1' } };
      if (call.url === '/api/portal/keys') return { status: 201, body: { id: '1', key: 'mcpal_x' } };
      return undefined;
    });

    await api.createKey('a');
    await api.createKey('b');

    const csrfCalls = calls.filter((c) => c.url === '/api/portal/csrf');
    const posts = calls.filter((c) => c.method === 'POST');
    expect(csrfCalls).toHaveLength(1);
    expect(posts).toHaveLength(2);
    expect(posts.every((c) => c.headers['X-CSRF-TOKEN'] === 'tok-1')).toBe(true);
  });

  it('sends purpose, allowed servers and expiry when creating a key', async () => {
    const calls = mockFetch((call) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : { status: 201, body: {} }));

    await api.createKey('team', { purpose: 'client', allowedServers: ['jira'], expiresAt: '2030-01-01T00:00:00.000Z' });

    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({
      name: 'team',
      purpose: 'client',
      allowedServers: ['jira'],
      expiresAt: '2030-01-01T00:00:00.000Z',
    });
  });

  it('fetches a new token after accepting an invitation because that signs the new user in', async () => {
    let tokenNumber = 0;
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: `tok-${++tokenNumber}` } };
      return { status: 201, body: {} };
    });

    await api.acceptInvitation('invitation', 'a-long-password');
    await api.createKey('k');

    expect(calls.find((c) => c.url === '/api/portal/keys')?.headers['X-CSRF-TOKEN']).toBe('tok-2');
  });

  it('does not send an anti-forgery header on GET calls', async () => {
    const calls = mockFetch(() => ({ body: [] }));

    await api.keys();

    expect(calls[0]?.headers['X-CSRF-TOKEN']).toBeUndefined();
  });

  it('fetches a new token after signing in because tokens are bound to the user', async () => {
    let tokenNumber = 0;
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: `tok-${++tokenNumber}` } };
      if (call.url === '/api/portal/auth/login') return { body: { email: 'a@b.c', companyId: '1', companyName: 'Acme' } };
      if (call.url === '/api/portal/keys') return { status: 201, body: {} };
      return undefined;
    });

    await api.login('a@b.c', 'pw');
    await api.createKey('k');

    const keyCall = calls.find((c) => c.url === '/api/portal/keys');
    expect(keyCall?.headers['X-CSRF-TOKEN']).toBe('tok-2');
  });

  it('returns null from me() when nobody is signed in', async () => {
    mockFetch(() => ({ status: 401 }));

    await expect(api.me()).resolves.toBeNull();
  });

  it('throws an ApiError with the server messages', async () => {
    mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      return { status: 400, body: { errors: ['Passwords must be longer.', 'Email is invalid.'] } };
    });

    const error = await api.signup('Acme', 'x', 'y').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).errors).toEqual(['Passwords must be longer.', 'Email is invalid.']);
    expect((error as ApiError).status).toBe(400);
  });

  it('reads OAuth style errors', async () => {
    mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      return { status: 401, body: { error: 'invalid_key', error_description: 'The API key is invalid.' } };
    });

    const error = await api.authorize({}).catch((e: unknown) => e);

    expect((error as ApiError).code).toBe('invalid_key');
    expect((error as ApiError).message).toBe('The API key is invalid.');
  });

  it('handles 204 responses without a body', async () => {
    mockFetch((call) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : { status: 204 }));

    await expect(api.revokeKey('abc')).resolves.toBeUndefined();
  });
});
