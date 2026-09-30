import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import type { ApiKey } from '../api/types';
import { mockFetch } from '../test/fetchMock';
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
  name: 'HQ agent',
  prefix: 'mcpal_1234567',
  createdAt: '2026-01-01T10:00:00Z',
  expiresAt: null,
  lastUsedAt: null,
  disabled: false,
};

describe('KeysPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists keys with prefix only', async () => {
    mockFetch(() => ({ body: [existing] }));

    renderPage();

    const row = (await screen.findByText('HQ agent')).closest('tr');
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
      return { body: [existing] };
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));

    expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/keys/k1')).toBe(true);
  });

  it('does not revoke when the confirmation is declined', async () => {
    vi.stubGlobal('confirm', vi.fn(() => false));
    const calls = mockFetch(() => ({ body: [existing] }));
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Revoke' }));

    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
  });
});
