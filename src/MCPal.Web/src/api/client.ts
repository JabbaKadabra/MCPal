import type { ApiKey, AuthorizeContext, ConnectInfo, Connection, CreatedApiKey, Me } from './types';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly errors: string[],
    readonly code?: string,
  ) {
    super(errors[0] ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
  }
}

let csrfToken: string | null = null;

async function getCsrfToken(): Promise<string> {
  if (csrfToken === null) {
    const response = await fetch('/api/portal/csrf', { credentials: 'same-origin' });
    const body = (await response.json()) as { token: string };
    csrfToken = body.token;
  }
  return csrfToken;
}

/** Forget the cached anti-forgery token, e.g. in tests. */
export function resetCsrfToken(): void {
  csrfToken = null;
}

function extractErrors(body: unknown, fallback: string): { errors: string[]; code?: string } {
  if (typeof body === 'object' && body !== null) {
    const record = body as Record<string, unknown>;
    const code = typeof record.error === 'string' ? record.error : undefined;
    if (Array.isArray(record.errors)) {
      return { errors: record.errors.map(String), code };
    }
    if (typeof record.error_description === 'string') {
      return { errors: [record.error_description], code };
    }
  }
  return { errors: [fallback] };
}

async function request<T>(method: 'GET' | 'POST' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (method !== 'GET') {
    headers['X-CSRF-TOKEN'] = await getCsrfToken();
  }

  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  });

  // Anti-forgery tokens are bound to the signed-in user: fetch a new one after sign-in, sign-up and sign-out.
  if (path.startsWith('/api/portal/auth/') && method !== 'GET') {
    csrfToken = null;
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  const json: unknown = text.length > 0 ? JSON.parse(text) : undefined;
  if (!response.ok) {
    const { errors, code } = extractErrors(json, response.statusText);
    throw new ApiError(response.status, errors, code);
  }
  return json as T;
}

export const api = {
  /** Returns null when nobody is signed in. */
  async me(): Promise<Me | null> {
    try {
      return await request<Me>('GET', '/api/portal/auth/me');
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        return null;
      }
      throw error;
    }
  },
  signup: (companyName: string, email: string, password: string) =>
    request<Me>('POST', '/api/portal/auth/signup', { companyName, email, password }),
  login: (email: string, password: string) => request<Me>('POST', '/api/portal/auth/login', { email, password }),
  logout: () => request<void>('POST', '/api/portal/auth/logout'),
  keys: () => request<ApiKey[]>('GET', '/api/portal/keys'),
  createKey: (name: string) => request<CreatedApiKey>('POST', '/api/portal/keys', { name }),
  revokeKey: (id: string) => request<void>('DELETE', `/api/portal/keys/${id}`),
  connections: () => request<Connection[]>('GET', '/api/portal/connections'),
  connectInfo: () => request<ConnectInfo>('GET', '/api/portal/connect-info'),
  authorizeContext: (search: string) => request<AuthorizeContext>('GET', `/api/oauth/authorize/context${search}`),
  authorize: (body: Record<string, unknown>) => request<{ redirectUrl: string }>('POST', '/api/oauth/authorize', body),
};
