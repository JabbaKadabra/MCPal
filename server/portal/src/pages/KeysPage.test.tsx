import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import type { ApiKey } from '../api/types';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { KeysPage } from './KeysPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <KeysPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const existing: ApiKey = {
  id: 'k1',
  name: 'HQ bridge',
  prefix: 'mcpal_1234567',
  createdAt: '2026-01-01T10:00:00Z',
  expiresAt: null,
  lastUsedAt: null,
  disabled: false,
  purpose: 'bridge',
  allowedServers: [],
};

/** The page reads keys and, for server suggestions, connections. */
function withConnections(keys: ApiKey[], role: 'owner' | 'member' = 'owner') {
  return (call: RecordedCall) => {
    if (call.url === '/api/portal/connections') return { body: [] };
    if (call.url === '/api/portal/auth/me') return { body: { email: 'a@b.c', companyId: 'c', companyName: 'Acme', role } };
    return { body: keys };
  };
}

describe('KeysPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists keys with prefix only', async () => {
    mockFetch(withConnections([existing]));

    renderPage();

    const row = (await screen.findByText('HQ bridge')).closest('tr');
    expect(row).not.toBeNull();
    if (row === null) return;
    expect(within(row).getByText('mcpal_1234567…')).toBeInTheDocument();
    expect(within(row).getByText('Active')).toBeInTheDocument();
  });

  it('shows the full key once after creation', async () => {
    let created = false;
    mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'POST') {
        created = true;
        return { status: 201, body: { ...existing, id: 'k2', name: 'New', key: 'mcpal_12345678_secretsecretsecret' } };
      }
      if (call.url === '/api/portal/connections') return { body: [] };
      return { body: created ? [existing] : [] };
    });
    const user = userEvent.setup();
    renderPage();

    await user.type(await screen.findByLabelText('Key name'), 'New');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    expect(await screen.findByTestId('created-key')).toHaveTextContent('mcpal_12345678_secretsecretsecret');
    expect(screen.getByText('Copy your new key now')).toBeInTheDocument();
  });

  it('revokes a key after confirmation', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'DELETE') return { status: 204 };
      return withConnections([existing])(call);
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));

    expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/keys/k1')).toBe(true);
  });

  it('does not revoke when the confirmation is declined', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    const calls = mockFetch(withConnections([existing]));
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));

    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('shows purpose and server restrictions in the key list', async () => {
    const restricted: ApiKey = { ...existing, id: 'k2', name: 'Jira team', purpose: 'client', allowedServers: ['jira', 'wiki'] };
    const anyKey: ApiKey = { ...existing, id: 'k3', name: 'Old key', purpose: 'any', allowedServers: [] };
    mockFetch(withConnections([existing, restricted, anyKey]));

    renderPage();

    const bridgeRow = (await screen.findByText('HQ bridge')).closest('tr');
    const clientRow = screen.getByText('Jira team').closest('tr');
    const anyRow = screen.getByText('Old key').closest('tr');
    if (bridgeRow === null || clientRow === null || anyRow === null) throw new Error('rows missing');
    expect(within(bridgeRow).getByText('Bridge')).toBeInTheDocument();
    expect(within(clientRow).getByText('Claude')).toBeInTheDocument();
    expect(within(clientRow).getByText('jira, wiki')).toBeInTheDocument();
    expect(within(anyRow).getByText('Bridge and Claude')).toBeInTheDocument();
    expect(within(anyRow).getByText('All servers')).toBeInTheDocument();
  });

  it('creates a Claude key by default and sends purpose, servers and expiry', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'POST') return { status: 201, body: { ...existing, id: 'k9', key: 'mcpal_12345678_x', purpose: 'client', allowedServers: ['jira'] } };
      if (call.url === '/api/portal/connections') return { body: [] };
      return { body: [] };
    });
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByLabelText('Used by')).toHaveValue('client');
    await user.type(screen.getByLabelText('Key name'), 'Jira team');
    await user.type(screen.getByLabelText('Allowed servers'), 'jira, Wiki ,');
    await user.type(screen.getByLabelText('Expires on'), '2030-01-31');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await screen.findByTestId('created-key');
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ name: 'Jira team', purpose: 'client', allowedServers: ['jira', 'Wiki'] });
    const expiresAt = (post?.body as { expiresAt: string }).expiresAt;
    expect(new Date(expiresAt).getFullYear()).toBe(2030);
  });

  it('creates a bridge key without server restrictions and hides that field', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'POST') return { status: 201, body: { ...existing, id: 'k9', key: 'mcpal_12345678_x' } };
      return { body: [] };
    });
    const user = userEvent.setup();
    renderPage();

    await user.selectOptions(await screen.findByLabelText('Used by'), 'bridge');
    expect(screen.queryByLabelText('Allowed servers')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Key name'), 'HQ bridge');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await screen.findByTestId('created-key');
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ purpose: 'bridge', allowedServers: [] });
    expect((post?.body as { expiresAt?: string }).expiresAt).toBeUndefined();
  });

  it('suggests the servers of connected bridges', async () => {
    mockFetch((call) => {
      if (call.url === '/api/portal/connections') {
        return { body: [{ bridgeName: 'hq', connectedAt: '2026-01-01T10:00:00Z', apiKeyName: null, servers: [{ name: 'jira', tools: ['a'] }], rejected: [], rejectedTools: [] }] };
      }
      return { body: [] };
    });

    const { container } = renderPage();

    await screen.findByLabelText('Allowed servers');
    await vi.waitFor(() => expect(container.querySelector('datalist option[value="jira"]')).not.toBeNull());
  });

  it('offers members only keys for Claude', async () => {
    mockFetch(withConnections([], 'member'));

    renderPage();

    const select = await screen.findByLabelText('Used by');
    await vi.waitFor(() => expect(within(select).queryByRole('option', { name: 'A bridge in your network' })).not.toBeInTheDocument());
    expect(within(select).getByRole('option', { name: 'Claude (users)' })).toBeInTheDocument();
    expect(within(select).queryByRole('option', { name: /legacy/ })).not.toBeInTheDocument();
  });

  it('shows who created a key to owners', async () => {
    mockFetch(withConnections([{ ...existing, createdBy: 'member@acme.example' }]));

    renderPage();

    const row = (await screen.findByText('HQ bridge')).closest('tr');
    if (row === null) throw new Error('row missing');
    expect(within(row).getByText('member@acme.example')).toBeInTheDocument();
  });
});
