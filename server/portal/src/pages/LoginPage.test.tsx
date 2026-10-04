import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import { mockFetch } from '../test/fetchMock';
import { LoginPage } from './LoginPage';

function LocationProbe() {
  const location = useLocation();
  return <p data-testid="location">{`${location.pathname}${location.search}`}</p>;
}

function renderLogin(url: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const me = { email: 'anna@acme.example', companyId: 'c', companyName: 'Acme', role: 'member' };

async function signIn() {
  const user = userEvent.setup();
  await user.type(screen.getByLabelText('Email'), 'anna@acme.example');
  await user.type(screen.getByLabelText('Password'), 'correct-horse-battery');
  await user.click(screen.getByRole('button', { name: 'Sign in' }));
}

describe('LoginPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('goes to the API keys page after a normal sign-in', async () => {
    mockFetch((call) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : { body: me }));
    renderLogin('/login');

    await signIn();

    expect(await screen.findByTestId('location')).toHaveTextContent('/keys');
  });

  it('returns to the page the user came from, including its query', async () => {
    mockFetch((call) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : { body: me }));
    const returnUrl = encodeURIComponent('/oauth/authorize?client_id=a&state=b');
    renderLogin(`/login?returnUrl=${returnUrl}`);

    await signIn();

    expect(await screen.findByTestId('location')).toHaveTextContent('/oauth/authorize?client_id=a&state=b');
  });

  it('ignores a return URL that leaves the app', async () => {
    mockFetch((call) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : { body: me }));
    renderLogin(`/login?returnUrl=${encodeURIComponent('https://evil.example/phish')}`);

    await signIn();

    expect(await screen.findByTestId('location')).toHaveTextContent('/keys');
  });

  it('tells Claude users to ask for an invitation instead of offering sign-up', () => {
    mockFetch(() => undefined);

    renderLogin(`/login?returnUrl=${encodeURIComponent('/oauth/authorize?client_id=a')}`);

    expect(screen.getByText('No account yet? Ask your admin for an invitation.')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Create account' })).not.toBeInTheDocument();
  });
});
