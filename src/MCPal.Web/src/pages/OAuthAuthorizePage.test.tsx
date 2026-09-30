import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import { mockFetch } from '../test/fetchMock';
import { OAuthAuthorizePage } from './OAuthAuthorizePage';

const search =
  '?client_id=abc&redirect_uri=https%3A%2F%2Fclaude.ai%2Fapi%2Fmcp%2Fauth_callback&response_type=code' +
  '&code_challenge=chal&code_challenge_method=S256&state=st';

function renderPage(url: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[`/oauth/authorize${url}`]}>
        <OAuthAuthorizePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
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

  it('shows the client name and where the user returns to', async () => {
    mockFetch(() => ({ body: { clientName: 'Claude', redirectHost: 'claude.ai', signedInCompany: null } }));

    renderPage(search);

    expect(await screen.findByRole('heading', { name: 'Connect Claude to MCPal' })).toBeInTheDocument();
    expect(screen.getByText(/sent back to claude\.ai/)).toBeInTheDocument();
  });

  it('submits the pasted API key and follows the returned redirect', async () => {
    const calls = mockFetch((call) => {
      if (call.url.startsWith('/api/oauth/authorize/context')) {
        return { body: { clientName: 'Claude', redirectHost: 'claude.ai', signedInCompany: null } };
      }
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.url === '/api/oauth/authorize') return { body: { redirectUrl: 'https://claude.ai/api/mcp/auth_callback?code=xyz&state=st' } };
      return undefined;
    });
    const user = userEvent.setup();
    renderPage(search);

    await user.type(await screen.findByLabelText('API key'), 'mcpal_abc_key');
    await user.click(screen.getByRole('button', { name: 'Connect' }));

    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://claude.ai/api/mcp/auth_callback?code=xyz&state=st'));
    const submit = calls.find((c) => c.url === '/api/oauth/authorize');
    expect(submit?.body).toMatchObject({ client_id: 'abc', api_key: 'mcpal_abc_key', code_challenge: 'chal' });
  });

  it('shows a clear message when the key is rejected', async () => {
    mockFetch((call) => {
      if (call.url.startsWith('/api/oauth/authorize/context')) {
        return { body: { clientName: 'Claude', redirectHost: 'claude.ai', signedInCompany: null } };
      }
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      return { status: 401, body: { error: 'invalid_key', error_description: 'bad key' } };
    });
    const user = userEvent.setup();
    renderPage(search);

    await user.type(await screen.findByLabelText('API key'), 'mcpal_wrong');
    await user.click(screen.getByRole('button', { name: 'Connect' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('That API key is invalid, expired or revoked.');
    expect(assign).not.toHaveBeenCalled();
  });

  it('offers to connect as the signed-in company', async () => {
    const calls = mockFetch((call) => {
      if (call.url.startsWith('/api/oauth/authorize/context')) {
        return { body: { clientName: 'Claude', redirectHost: 'claude.ai', signedInCompany: 'Acme GmbH' } };
      }
      if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
      if (call.url === '/api/oauth/authorize') return { body: { redirectUrl: 'https://claude.ai/cb?code=1' } };
      return undefined;
    });
    const user = userEvent.setup();
    renderPage(search);

    await user.click(await screen.findByRole('button', { name: 'Connect as Acme GmbH' }));

    await waitFor(() => expect(assign).toHaveBeenCalledWith('https://claude.ai/cb?code=1'));
    expect(calls.find((c) => c.url === '/api/oauth/authorize')?.body).toMatchObject({ use_session: true });
  });

  it('sends the user back with access_denied when they cancel', async () => {
    mockFetch(() => ({ body: { clientName: 'Claude', redirectHost: 'claude.ai', signedInCompany: null } }));
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
