import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import type { Me, Team } from '../api/types';
import { Layout } from '../components/Layout';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { AcceptInvitationPage } from './AcceptInvitationPage';
import { UsersPage } from './UsersPage';

function renderAt(path: string, routes: React.ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>{routes}</Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function csrfThen(handler: (call: RecordedCall) => { status?: number; body?: unknown } | undefined) {
  return (call: RecordedCall) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : handler(call));
}

const team: Team = {
  users: [
    { id: 'u1', email: 'owner@acme.example', role: 'owner', emailConfirmed: true, disabled: false },
    { id: 'u2', email: 'member@acme.example', role: 'member', emailConfirmed: true, disabled: false },
  ],
  invitations: [{ id: 'i1', email: 'pending@acme.example', role: 'member', expiresAt: '2026-10-07T10:00:00Z', createdAt: '2026-09-30T10:00:00Z', invitedBy: 'owner@acme.example' }],
};

const owner: Me = { email: 'owner@acme.example', companyId: 'c1', companyName: 'Acme', role: 'owner' };

describe('team pages', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('UsersPage', () => {
    const page = <Route path="/users" element={<UsersPage />} />;

    it('lists users with roles and pending invitations', async () => {
      mockFetch(() => ({ body: team }));

      renderAt('/users', page);

      const memberRow = (await screen.findByText('member@acme.example')).closest('tr');
      expect(memberRow).not.toBeNull();
      if (memberRow === null) return;
      expect(within(memberRow).getByText('Member')).toBeInTheDocument();
      expect(screen.getByText('pending@acme.example')).toBeInTheDocument();
    });

    it('invites a colleague with the chosen role', async () => {
      const calls = mockFetch(
        csrfThen((call) => {
          if (call.method === 'POST') return { status: 201, body: team.invitations[0] };
          return { body: team };
        }),
      );
      const user = userEvent.setup();
      renderAt('/users', page);

      await user.type(await screen.findByLabelText('Email address'), 'new@acme.example');
      await user.selectOptions(screen.getByLabelText('Role'), 'owner');
      await user.click(screen.getByRole('button', { name: 'Send invitation' }));

      await waitFor(() => expect(calls.find((c) => c.url === '/api/portal/users' && c.method === 'POST')?.body).toEqual({ email: 'new@acme.example', role: 'owner' }));
    });

    it('shows the reason when the invitation is refused', async () => {
      mockFetch(csrfThen((call) => (call.method === 'POST' ? { status: 400, body: { errors: ['This email address cannot be invited.'] } } : { body: team })));
      const user = userEvent.setup();
      renderAt('/users', page);

      await user.type(await screen.findByLabelText('Email address'), 'taken@acme.example');
      await user.click(screen.getByRole('button', { name: 'Send invitation' }));

      expect(await screen.findByText('This email address cannot be invited.')).toBeInTheDocument();
    });

    it('removes a user after confirmation and shows the last-owner refusal', async () => {
      vi.stubGlobal('confirm', vi.fn(() => true));
      const calls = mockFetch(
        csrfThen((call) => {
          if (call.method === 'DELETE' && call.url === '/api/portal/users/u1') return { status: 400, body: { errors: ['The last owner cannot be removed. Make someone else an owner first.'] } };
          if (call.method === 'DELETE') return { status: 204 };
          return { body: team };
        }),
      );
      const user = userEvent.setup();
      renderAt('/users', page);

      const memberRow = (await screen.findByText('member@acme.example')).closest('tr');
      const ownerRow = screen.getByText('owner@acme.example').closest('tr');
      if (memberRow === null || ownerRow === null) throw new Error('rows missing');
      await user.click(within(memberRow).getByRole('button', { name: 'Remove' }));
      await user.click(within(ownerRow).getByRole('button', { name: 'Remove' }));

      expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/users/u2')).toBe(true);
      expect(await screen.findByText(/The last owner cannot be removed/)).toBeInTheDocument();
    });

    it('does not remove anybody when the confirmation is declined', async () => {
      vi.stubGlobal('confirm', vi.fn(() => false));
      const calls = mockFetch(() => ({ body: team }));
      const user = userEvent.setup();
      renderAt('/users', page);

      const memberRow = (await screen.findByText('member@acme.example')).closest('tr');
      if (memberRow === null) throw new Error('row missing');
      await user.click(within(memberRow).getByRole('button', { name: 'Remove' }));

      expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    });

    it('disables a user after confirmation and offers to enable a disabled one', async () => {
      vi.stubGlobal('confirm', vi.fn(() => true));
      const withDisabled: Team = { ...team, users: [team.users[0] ?? (() => { throw new Error('fixture'); })(), { id: 'u3', email: 'off@acme.example', role: 'member', emailConfirmed: true, disabled: true }, ...team.users.slice(1)] };
      const calls = mockFetch(
        csrfThen((call) => {
          if (call.method === 'POST') return { body: team.users[1] };
          return { body: withDisabled };
        }),
      );
      const user = userEvent.setup();
      renderAt('/users', page);

      const activeRow = (await screen.findByText('member@acme.example')).closest('tr');
      const disabledRow = screen.getByText('off@acme.example').closest('tr');
      if (activeRow === null || disabledRow === null) throw new Error('rows missing');
      expect(within(disabledRow).getByText('Disabled')).toBeInTheDocument();
      await user.click(within(activeRow).getByRole('button', { name: 'Disable' }));
      await user.click(within(disabledRow).getByRole('button', { name: 'Enable' }));

      await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/portal/users/u2/disable')).toBe(true));
      await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/portal/users/u3/enable')).toBe(true));
    });

    it('cancels a pending invitation', async () => {
      const calls = mockFetch(csrfThen((call) => (call.method === 'DELETE' ? { status: 204 } : { body: team })));
      const user = userEvent.setup();
      renderAt('/users', page);

      const row = (await screen.findByText('pending@acme.example')).closest('tr');
      if (row === null) throw new Error('row missing');
      await user.click(within(row).getByRole('button', { name: 'Cancel invitation' }));

      await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/invitations/i1')).toBe(true));
    });
  });

  describe('AcceptInvitationPage', () => {
    const page = (
      <>
        <Route path="/accept-invitation" element={<AcceptInvitationPage />} />
        <Route path="/keys" element={<p>keys page</p>} />
      </>
    );

    it('shows who invites and creates the account with the chosen password', async () => {
      const calls = mockFetch(
        csrfThen((call) => {
          if (call.url.startsWith('/api/portal/invitations/preview')) return { body: { companyName: 'Acme GmbH', email: 'new@acme.example', role: 'member' } };
          if (call.url === '/api/portal/invitations/accept') return { status: 201, body: { email: 'new@acme.example', companyId: 'c1', companyName: 'Acme GmbH', role: 'member' } };
          return undefined;
        }),
      );
      const user = userEvent.setup();
      renderAt('/accept-invitation?token=tok-123', page);

      expect(await screen.findByText(/Acme GmbH/)).toBeInTheDocument();
      expect(screen.getByText('new@acme.example')).toBeInTheDocument();
      await user.type(screen.getByLabelText('Password (at least 10 characters)'), 'a-long-password-1');
      await user.type(screen.getByLabelText('Repeat the password'), 'a-long-password-1');
      await user.click(screen.getByRole('button', { name: 'Join' }));

      expect(await screen.findByText('keys page')).toBeInTheDocument();
      expect(calls.find((c) => c.url === '/api/portal/invitations/preview?token=tok-123')).toBeDefined();
      expect(calls.find((c) => c.url === '/api/portal/invitations/accept')?.body).toEqual({ token: 'tok-123', password: 'a-long-password-1' });
    });

    it('refuses different passwords without asking the server', async () => {
      const calls = mockFetch(csrfThen(() => ({ body: { companyName: 'Acme GmbH', email: 'new@acme.example', role: 'member' } })));
      const user = userEvent.setup();
      renderAt('/accept-invitation?token=tok-123', page);

      await user.type(await screen.findByLabelText('Password (at least 10 characters)'), 'a-long-password-1');
      await user.type(screen.getByLabelText('Repeat the password'), 'something-else-entirely');
      await user.click(screen.getByRole('button', { name: 'Join' }));

      expect(await screen.findByText('The two passwords are not the same.')).toBeInTheDocument();
      expect(calls.some((c) => c.url === '/api/portal/invitations/accept')).toBe(false);
    });

    it('explains an invalid or expired invitation', async () => {
      mockFetch(() => ({ status: 400, body: { errors: ['This invitation is invalid or has expired.'] } }));

      renderAt('/accept-invitation?token=old', page);

      expect(await screen.findByText('This invitation is invalid or has expired.')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Join' })).not.toBeInTheDocument();
    });

    it('explains a link without token', () => {
      mockFetch(() => undefined);

      renderAt('/accept-invitation', page);

      expect(screen.getByText('This link is not valid. Ask the person who invited you for a new one.')).toBeInTheDocument();
    });
  });

  describe('Layout', () => {
    function renderLayout(me: Me) {
      mockFetch((call) => (call.url === '/api/portal/auth/me' ? { body: me } : { body: [] }));
      return renderAt('/keys', <Route element={<Layout />}><Route path="/keys" element={<p>content</p>} /></Route>);
    }

    it('shows the users page to owners', async () => {
      renderLayout(owner);

      expect(await screen.findByRole('link', { name: 'Users' })).toBeInTheDocument();
    });

    it('hides the users page from members', async () => {
      renderLayout({ ...owner, role: 'member' });

      await screen.findByText('Acme');
      expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument();
    });
  });
});
