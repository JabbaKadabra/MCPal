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

  it('shows the type and the user of a key to owners', async () => {
    const personal: ApiKey = { ...existing, id: 'k2', name: 'My laptop', purpose: 'personal', userEmail: 'anna@acme.example' };
    mockFetch(withConnections([existing, personal]));

    renderPage();

    const bridgeRow = (await screen.findByText('HQ bridge')).closest('tr');
    const personalRow = screen.getByText('My laptop').closest('tr');
    if (bridgeRow === null || personalRow === null) throw new Error('rows missing');
    expect(within(bridgeRow).getByText('Bridge')).toBeInTheDocument();
    expect(within(personalRow).getByText('Personal')).toBeInTheDocument();
    expect(within(personalRow).getByText('anna@acme.example')).toBeInTheDocument();
  });

  it('creates a personal access token by default and sends purpose and expiry', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'POST') return { status: 201, body: { ...existing, id: 'k9', key: 'mcpal_12345678_x', purpose: 'personal' } };
      return { body: [] };
    });
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByLabelText('Type')).toHaveValue('personal');
    await user.type(screen.getByLabelText('Key name'), 'Laptop');
    await user.type(screen.getByLabelText('Expires on'), '2030-01-31');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await screen.findByTestId('created-key');
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ name: 'Laptop', purpose: 'personal' });
    expect(post?.body).not.toHaveProperty('allowedServers');
    const expiresAt = (post?.body as { expiresAt: string }).expiresAt;
    expect(new Date(expiresAt).getFullYear()).toBe(2030);
  });

  it('creates a bridge key without expiry', async () => {
    const calls = mockFetch((call) => {
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.method === 'POST') return { status: 201, body: { ...existing, id: 'k9', key: 'mcpal_12345678_x' } };
      return { body: [] };
    });
    const user = userEvent.setup();
    renderPage();

    await user.selectOptions(await screen.findByLabelText('Type'), 'bridge');
    await user.type(screen.getByLabelText('Key name'), 'HQ bridge');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await screen.findByTestId('created-key');
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ purpose: 'bridge' });
    expect((post?.body as { expiresAt?: string }).expiresAt).toBeUndefined();
  });

  it('offers members only personal access tokens', async () => {
    mockFetch(withConnections([], 'member'));

    renderPage();

    const select = await screen.findByLabelText('Type');
    await vi.waitFor(() => expect(within(select).queryByRole('option', { name: /Bridge key/ })).not.toBeInTheDocument());
    expect(within(select).getByRole('option', { name: /Personal access token/ })).toBeInTheDocument();
  });

  it('shows who created a key to owners', async () => {
    mockFetch(withConnections([{ ...existing, createdBy: 'member@acme.example' }]));

    renderPage();

    const row = (await screen.findByText('HQ bridge')).closest('tr');
    if (row === null) throw new Error('row missing');
    expect(within(row).getByText('member@acme.example')).toBeInTheDocument();
  });
});
