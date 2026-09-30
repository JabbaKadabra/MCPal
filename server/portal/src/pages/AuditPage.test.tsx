import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ApiKey, AuditEntry, AuditPage as AuditPageData } from '../api/types';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { AuditPage } from './AuditPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AuditPage />
    </QueryClientProvider>,
  );
}

const key: ApiKey = {
  id: 'k1',
  name: 'HQ bridge',
  prefix: 'mcpal_1234567',
  createdAt: '2026-01-01T10:00:00Z',
  expiresAt: null,
  lastUsedAt: null,
  disabled: false,
  purpose: 'personal',
};

function entry(overrides: Partial<AuditEntry>): AuditEntry {
  return {
    id: 'e1',
    occurredAt: '2026-09-30T08:00:00Z',
    durationMs: 120,
    authKind: 'pat',
    userId: 'u1',
    userEmail: 'anna@acme.example',
    apiKeyId: 'k1',
    apiKeyName: 'HQ bridge',
    oauthClientId: null,
    bridgeName: 'hq-01',
    serverName: 'kb',
    toolName: 'search',
    publicName: 'kb__search',
    outcome: 'ok',
    errorMessage: null,
    ...overrides,
  };
}

const team = {
  users: [
    { id: 'u1', email: 'anna@acme.example', role: 'member', emailConfirmed: true, disabled: false, groups: [] },
    { id: 'u2', email: 'ben@acme.example', role: 'member', emailConfirmed: true, disabled: false, groups: [] },
  ],
  invitations: [],
};

/** The page reads the audit log, keys and users (for the filters). */
function scripted(page: AuditPageData) {
  return (call: RecordedCall) => {
    if (call.url === '/api/portal/keys') return { body: [key] };
    if (call.url === '/api/portal/users') return { body: team };
    return { body: page };
  };
}

function auditCalls(calls: RecordedCall[]): RecordedCall[] {
  return calls.filter((call) => call.url.startsWith('/api/portal/audit'));
}

describe('AuditPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the calls with caller, bridge, duration and outcome', async () => {
    const page: AuditPageData = {
      items: [
        entry({ id: 'e2', publicName: 'kb__get', outcome: 'tool_error', errorMessage: 'backend exploded', apiKeyId: null, apiKeyName: null, authKind: 'oauth', oauthClientId: 'claude-1' }),
        entry({}),
      ],
      nextCursor: null,
    };
    mockFetch(scripted(page));

    renderPage();

    const failed = (await screen.findByText('kb__get')).closest('tr');
    expect(failed).not.toBeNull();
    if (failed === null) return;
    expect(within(failed).getByText('claude-1')).toBeInTheDocument();
    expect(within(failed).getByText('Tool error')).toBeInTheDocument();
    expect(within(failed).getByText('backend exploded')).toBeInTheDocument();
    const ok = screen.getByText('kb__search').closest('tr');
    expect(ok).not.toBeNull();
    if (ok === null) return;
    expect(within(ok).getByText('HQ bridge')).toBeInTheDocument();
    expect(within(ok).getByText('hq-01')).toBeInTheDocument();
    expect(within(ok).getByText('120 ms')).toBeInTheDocument();
    expect(within(ok).getByText('OK')).toBeInTheDocument();
  });

  it('shows which user made each call, and marks removed users', async () => {
    mockFetch(
      scripted({
        items: [entry({ id: 'e2', publicName: 'kb__ghost', userId: 'gone', userEmail: null }), entry({}), entry({ id: 'e3', publicName: 'kb__legacy', userId: null, userEmail: null })],
        nextCursor: null,
      }),
    );

    renderPage();

    const ghost = (await screen.findByText('kb__ghost')).closest('tr');
    const current = screen.getByText('kb__search').closest('tr');
    if (ghost === null || current === null) throw new Error('rows missing');
    expect(within(ghost).getByText('(removed user)')).toBeInTheDocument();
    expect(within(current).getByText('anna@acme.example')).toBeInTheDocument();
  });

  it('applies the filters to the request', async () => {
    const calls = mockFetch(scripted({ items: [], nextCursor: null }));
    renderPage();
    await screen.findByText('No calls match these filters.');

    await userEvent.type(screen.getByLabelText('Tool'), 'search');
    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'timeout');
    await userEvent.selectOptions(screen.getByLabelText('API key'), 'k1');
    await userEvent.selectOptions(await screen.findByLabelText('User'), 'u2');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    const last = auditCalls(calls).at(-1);
    expect(last?.url).toContain('tool=search');
    expect(last?.url).toContain('outcome=timeout');
    expect(last?.url).toContain('keyId=k1');
    expect(last?.url).toContain('userId=u2');
  });

  it('loads the next page with the cursor', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/keys') return { body: [key] };
      if (call.url === '/api/portal/users') return { body: team };
      if (call.url.includes('cursor=c1')) return { body: { items: [entry({ id: 'e2', publicName: 'kb__older' })], nextCursor: null } };
      return { body: { items: [entry({})], nextCursor: 'c1' } };
    });
    renderPage();
    await screen.findByText('kb__search');

    await userEvent.click(screen.getByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('kb__older')).toBeInTheDocument();
    expect(screen.getByText('kb__search')).toBeInTheDocument();
    expect(auditCalls(calls).at(-1)?.url).toContain('cursor=c1');
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument();
  });

  it('links the CSV export with the current filters', async () => {
    mockFetch(scripted({ items: [entry({})], nextCursor: null }));
    renderPage();
    await screen.findByText('kb__search');

    await userEvent.selectOptions(screen.getByLabelText('Outcome'), 'offline');
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }));

    const link = screen.getByRole('link', { name: 'Export CSV' });
    expect(link.getAttribute('href')).toBe('/api/portal/audit/export.csv?outcome=offline');
  });

  it('shows the error message when loading fails', async () => {
    mockFetch((call) => (call.url === '/api/portal/keys' ? { body: [key] } : { status: 400, body: { errors: ["'outcome' must be valid."] } }));

    renderPage();

    expect(await screen.findByText("'outcome' must be valid.")).toBeInTheDocument();
  });
});
