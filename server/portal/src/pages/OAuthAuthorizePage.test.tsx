import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { OAuthAuthorizePage } from './OAuthAuthorizePage';

const search =
  '?client_id=abc&redirect_uri=https%3A%2F%2Fclaude.ai%2Fapi%2Fmcp%2Fauth_callback&response_type=code' +
  '&code_challenge=chal&code_challenge_method=S256&state=st';

function LocationProbe() {
  const location = useLocation();
  return <p data-testid="location">{`${location.pathname}${location.search}`}</p>;
}

function renderPage(url: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[`/oauth/authorize${url}`]}>
        <Routes>
          <Route path="/oauth/authorize" element={<OAuthAuthorizePage />} />
          <Route path="*" element={<LocationProbe />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const signedIn = { clientName: 'Claude', redirectHost: 'claude.ai', signedInEmail: 'anna@acme.example', signedInCompany: 'Acme GmbH' };
const signedOut = { clientName: 'Claude', redirectHost: 'claude.ai', signedInEmail: null, signedInCompany: null };

function script(context: unknown, extra?: (call: RecordedCall) => { status?: number; body?: unknown } | undefined) {
  return (call: RecordedCall) => {
    if (call.url.startsWith('/api/oauth/authorize/context')) return { body: context };
    if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
    return extra?.(call);
  };
}

describe('OAuthAuthorizePage', () => {
  const assign = vi.fn();

  beforeEach(() => {
    resetCsrfToken();
    assign.mockReset();
    vi.stubGlobal('location', { ...window.location, assign });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the client name, where the user returns to and who they connect as', async () => {
    mockFetch(script(signedIn));

    renderPage(search);

    expect(await screen.findByRole('heading', { name: 'Connect Claude to MCPal' })).toBeInTheDocument();
    expect(screen.getByText(/sent back to claude\.ai/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Connect as anna@acme.example (Acme GmbH)' })).toBeInTheDocument();
  });

  it('authorizes with the session only and follows the returned redirect', async () => {
    const calls = mockFetch(
      script(signedIn, (call) =>
        call.url === '/api/oauth/authorize' ? { body: { redirectUrl: 'https://claude.ai/api/mcp/auth_callback?code=xyz&state=st' } } : undefined,
      ),
    );
    const user = userEvent.setup();
    renderPage(search);

    await user.click(await screen.findByRole('button', { name: /Connect as anna@acme.example/ }));

    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://claude.ai/api/mcp/auth_callback?code=xyz&state=st'));
    const body = calls.find((c) => c.url === '/api/oauth/authorize')?.body;
    expect(body).toMatchObject({ client_id: 'abc', code_challenge: 'chal' });
    expect(body).not.toHaveProperty('api_key');
  });

  it('sends a user without a session to the login and back to this page afterwards', async () => {
    mockFetch(script(signedOut));

    renderPage(search);

    const location = await screen.findByTestId('location');
    expect(location).toHaveTextContent('/login?returnUrl=');
    const returnUrl = new URL(`http://x${location.textContent ?? ''}`).searchParams.get('returnUrl');
    expect(returnUrl).toBe(`/oauth/authorize${search}`);
  });

  it('signs out and returns to the login with "Not you?"', async () => {
    mockFetch(script(signedIn, (call) => (call.url === '/api/portal/auth/logout' ? { status: 204 } : undefined)));
    const user = userEvent.setup();
    renderPage(search);

    await user.click(await screen.findByRole('button', { name: 'Not you? Sign out' }));

    expect(await screen.findByTestId('location')).toHaveTextContent('/login?returnUrl=');
  });

  it('offers to sign in again when the session ended before the click', async () => {
    mockFetch(
      script(signedIn, (call) =>
        call.url === '/api/oauth/authorize' ? { status: 401, body: { error: 'login_required', error_description: 'Sign in to the portal first.' } } : undefined,
      ),
    );
    const user = userEvent.setup();
    renderPage(search);

    await user.click(await screen.findByRole('button', { name: /Connect as anna@acme.example/ }));

    await user.click(await screen.findByRole('button', { name: 'Sign in' }));
    expect(await screen.findByTestId('location')).toHaveTextContent('/login?returnUrl=');
    expect(assign).not.toHaveBeenCalled();
  });

  it('sends the user back with access_denied when they cancel', async () => {
    mockFetch(script(signedIn));
    const user = userEvent.setup();
    renderPage(search);

    await user.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(assign).toHaveBeenCalledTimes(1);
    const target = new URL(String(assign.mock.calls[0]?.[0]));
    expect(target.searchParams.get('error')).toBe('access_denied');
  });

  it('rejects links without client and redirect parameters', () => {
    mockFetch(() => undefined);

    renderPage('');

    expect(screen.getByRole('alert')).toHaveTextContent('This sign-in link is not valid.');
  });
});
