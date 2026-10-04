import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockFetch } from '../test/fetchMock';
import { Home } from './Home';

function renderHome() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/']}>
        <Routes>
          <Route path="/" element={<Home />} />
          <Route path="/setup" element={<p>setup page</p>} />
          <Route path="/keys" element={<p>keys page</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function backend(role: 'owner' | 'member', hasConnectedBridge: boolean) {
  return (call: { url: string }) => {
    if (call.url === '/api/portal/auth/me') return { body: { email: 'a@b.c', companyId: 'c', companyName: 'Acme', role } };
    if (call.url === '/api/portal/setup') return { body: { hasConnectedBridge } };
    return undefined;
  };
}

describe('Home', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends an owner whose company never had a bridge to the setup walkthrough', async () => {
    mockFetch(backend('owner', false));
    renderHome();
    expect(await screen.findByText('setup page')).toBeInTheDocument();
  });

  it('sends an owner of a company with a connected bridge to the API keys', async () => {
    mockFetch(backend('owner', true));
    renderHome();
    expect(await screen.findByText('keys page')).toBeInTheDocument();
  });

  it('sends a member to the API keys without asking for the setup', async () => {
    const calls = mockFetch(backend('member', false));
    renderHome();
    expect(await screen.findByText('keys page')).toBeInTheDocument();
    expect(calls.map((c) => c.url)).not.toContain('/api/portal/setup');
  });

  it('falls back to the API keys when the setup cannot be read', async () => {
    mockFetch((call) => (call.url === '/api/portal/auth/me' ? { body: { email: 'a@b.c', companyId: 'c', companyName: 'Acme', role: 'owner' } } : { status: 500, body: { errors: ['x'] } }));
    renderHome();
    expect(await screen.findByText('keys page')).toBeInTheDocument();
  });
});
