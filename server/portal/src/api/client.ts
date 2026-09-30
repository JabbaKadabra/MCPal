import type { ApiKey, AuditFilters, Invitation, InvitationPreview, NewApiKey, AuditPage, AuthorizeContext, ConnectInfo, Connection, CreatedApiKey, Me, Role, Team, TeamMember, Group, Grant, UserAccess } from './types';

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

async function request<T>(method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE', path: string, body?: unknown): Promise<T> {
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

  // Anti-forgery tokens are bound to the signed-in user: fetch a new one after sign-in, sign-up, sign-out and accepting an invitation.
  if ((path.startsWith('/api/portal/auth/') || path === '/api/portal/invitations/accept') && method !== 'GET') {
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

/** Query string of the audit endpoints: only filters that are set. */
export function auditQuery(filters: AuditFilters, cursor?: string): string {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(filters)) {
    if (value !== '') {
      params.set(name, value);
    }
  }
  if (cursor !== undefined) {
    params.set('cursor', cursor);
  }
  const text = params.toString();
  return text === '' ? '' : `?${text}`;
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
  updateMe: (displayName: string) => request<Me>('PATCH', '/api/portal/auth/me', { displayName }),
  confirmEmail: (userId: string, token: string) => request<void>('POST', '/api/portal/auth/confirm-email', { userId, token }),
  resendConfirmation: (email: string) => request<void>('POST', '/api/portal/auth/resend-confirmation', { email }),
  forgotPassword: (email: string) => request<void>('POST', '/api/portal/auth/forgot-password', { email }),
  resetPassword: (email: string, token: string, newPassword: string) =>
    request<void>('POST', '/api/portal/auth/reset-password', { email, token, newPassword }),
  team: () => request<Team>('GET', '/api/portal/users'),
  invite: (email: string, role: Role) => request<Invitation>('POST', '/api/portal/users', { email, role }),
  removeUser: (id: string) => request<void>('DELETE', `/api/portal/users/${encodeURIComponent(id)}`),
  disableUser: (id: string) => request<TeamMember>('POST', `/api/portal/users/${encodeURIComponent(id)}/disable`),
  enableUser: (id: string) => request<TeamMember>('POST', `/api/portal/users/${encodeURIComponent(id)}/enable`),
  cancelInvitation: (id: string) => request<void>('DELETE', `/api/portal/invitations/${id}`),
  invitationPreview: (token: string) =>
    request<InvitationPreview>('GET', `/api/portal/invitations/preview?token=${encodeURIComponent(token)}`),
  acceptInvitation: (token: string, password: string) => request<Me>('POST', '/api/portal/invitations/accept', { token, password }),
  groups: () => request<Group[]>('GET', '/api/portal/groups'),
  createGroup: (name: string) => request<Group>('POST', '/api/portal/groups', { name }),
  renameGroup: (id: string, name: string) => request<Group>('PATCH', `/api/portal/groups/${id}`, { name }),
  deleteGroup: (id: string) => request<void>('DELETE', `/api/portal/groups/${id}`),
  setGroupMembers: (id: string, userIds: string[]) => request<Group>('PUT', `/api/portal/groups/${id}/members`, { userIds }),
  addGrant: (groupId: string, serverPattern: string, toolPatterns: string[]) =>
    request<Grant>('POST', `/api/portal/groups/${groupId}/grants`, { serverPattern, toolPatterns }),
  updateGrant: (id: string, serverPattern: string, toolPatterns: string[]) =>
    request<Grant>('PUT', `/api/portal/grants/${id}`, { serverPattern, toolPatterns }),
  deleteGrant: (id: string) => request<void>('DELETE', `/api/portal/grants/${id}`),
  myAccess: () => request<UserAccess>('GET', '/api/portal/access/me'),
  userAccess: (id: string) => request<UserAccess>('GET', `/api/portal/access/users/${encodeURIComponent(id)}`),
  keys: () => request<ApiKey[]>('GET', '/api/portal/keys'),
  createKey: (name: string, options?: NewApiKey) => request<CreatedApiKey>('POST', '/api/portal/keys', { name, ...options }),
  revokeKey: (id: string) => request<void>('DELETE', `/api/portal/keys/${id}`),
  connections: () => request<Connection[]>('GET', '/api/portal/connections'),
  connectInfo: () => request<ConnectInfo>('GET', '/api/portal/connect-info'),
  audit: (filters: AuditFilters, cursor?: string) => request<AuditPage>('GET', `/api/portal/audit${auditQuery(filters, cursor)}`),
  authorizeContext: (search: string) => request<AuthorizeContext>('GET', `/api/oauth/authorize/context${search}`),
  authorize: (body: Record<string, unknown>) => request<{ redirectUrl: string }>('POST', '/api/oauth/authorize', body),
};
