import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { MyAccessPage } from './MyAccessPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <MyAccessPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const me = { email: 'anna@acme.example', companyId: 'c', companyName: 'Acme', role: 'member', displayName: 'Anna' };

function scripted(access: unknown, extra?: (call: RecordedCall) => { status?: number; body?: unknown } | undefined) {
  return (call: RecordedCall) => {
    if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
    const custom = extra?.(call);
    if (custom !== undefined) return custom;
    if (call.url === '/api/portal/access/me') return { body: access };
    if (call.url === '/api/portal/auth/me') return { body: me };
    return undefined;
  };
}

describe('MyAccessPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows role, groups and the tools the user may use', async () => {
    mockFetch(
      scripted({
        userId: 'u2',
        email: 'anna@acme.example',
        role: 'member',
        disabled: false,
        allTools: false,
        groups: ['Everyone', 'hr'],
        tools: [{ server: 'hr', tool: 'salaries', publicName: 'hr__salaries' }],
      }),
    );

    renderPage();

    expect(await screen.findByText('hr__salaries')).toBeInTheDocument();
    expect(screen.getByText('hr')).toBeInTheDocument();
    expect(screen.getByText('Everyone')).toBeInTheDocument();
  });

  it('says so when no tool is available, and that owners may use everything', async () => {
    mockFetch(scripted({ userId: 'u1', email: 'boss@acme.example', role: 'owner', disabled: false, allTools: true, groups: ['Everyone'], tools: [] }));

    renderPage();

    expect(await screen.findByText('Owners may use every tool of the company.')).toBeInTheDocument();
    expect(screen.getByText(/No tools are available to you right now/)).toBeInTheDocument();
  });

  it('saves the display name', async () => {
    const calls = mockFetch(
      scripted({ userId: 'u2', email: 'anna@acme.example', role: 'member', disabled: false, allTools: false, groups: ['Everyone'], tools: [] }, (call) =>
        call.method === 'PATCH' ? { body: { ...me, displayName: 'Anna Example' } } : undefined,
      ),
    );
    const user = userEvent.setup();
    renderPage();

    const field = await screen.findByLabelText('Display name');
    await waitFor(() => expect(field).toHaveValue('Anna'));
    await user.clear(field);
    await user.type(field, 'Anna Example');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(calls.find((c) => c.method === 'PATCH' && c.url === '/api/portal/auth/me')?.body).toEqual({ displayName: 'Anna Example' }));
  });
});
