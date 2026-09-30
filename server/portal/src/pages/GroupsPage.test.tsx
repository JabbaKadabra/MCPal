import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import type { Group, Team } from '../api/types';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { GroupsPage } from './GroupsPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <GroupsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const everyone: Group = { id: 'g0', name: 'Everyone', isEveryone: true, memberIds: [], grants: [{ id: 'x0', serverPattern: '*', toolPatterns: ['*'] }] };
const hr: Group = { id: 'g1', name: 'hr', isEveryone: false, memberIds: ['u2'], grants: [{ id: 'x1', serverPattern: 'hr', toolPatterns: ['list_*'] }] };

const team: Team = {
  users: [
    { id: 'u1', email: 'owner@acme.example', role: 'owner', emailConfirmed: true, disabled: false, groups: [] },
    { id: 'u2', email: 'anna@acme.example', role: 'member', emailConfirmed: true, disabled: false, groups: ['hr'] },
    { id: 'u3', email: 'ben@acme.example', role: 'member', emailConfirmed: true, disabled: false, groups: [] },
  ],
  invitations: [],
};

const connections = [
  {
    bridgeName: 'hq',
    bridgeVersion: '1.2',
    updateAvailable: false,
    latestBridgeVersion: null,
    connectedAt: '2026-01-01T10:00:00Z',
    apiKeyName: null,
    servers: [
      { name: 'hr', tools: ['list_staff', 'salaries'] },
      { name: 'wiki', tools: ['search'] },
    ],
    rejected: [],
    rejectedTools: [],
  },
];

function scripted(extra?: (call: RecordedCall) => { status?: number; body?: unknown } | undefined) {
  return (call: RecordedCall) => {
    if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
    const custom = extra?.(call);
    if (custom !== undefined) return custom;
    if (call.url === '/api/portal/groups' && call.method === 'GET') return { body: [everyone, hr] };
    if (call.url === '/api/portal/users') return { body: team };
    if (call.url === '/api/portal/connections') return { body: connections };
    return undefined;
  };
}

describe('GroupsPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists groups with their grants and marks Everyone as fixed', async () => {
    mockFetch(scripted());

    renderPage();

    const everyoneCard = await screen.findByRole('region', { name: 'Everyone' });
    expect(within(everyoneCard).getByText('All users')).toBeInTheDocument();
    expect(within(everyoneCard).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(everyoneCard).queryByRole('button', { name: 'Rename' })).not.toBeInTheDocument();
    const hrCard = screen.getByRole('region', { name: 'hr' });
    expect(within(hrCard).getByText('list_*')).toBeInTheDocument();
    expect(within(hrCard).getByRole('checkbox', { name: 'anna@acme.example' })).toBeChecked();
    expect(within(hrCard).getByRole('checkbox', { name: 'ben@acme.example' })).not.toBeChecked();
  });

  it('saves the member list when a user is ticked', async () => {
    const calls = mockFetch(scripted((call) => (call.method === 'PUT' ? { body: hr } : undefined)));
    const user = userEvent.setup();
    renderPage();

    const hrCard = await screen.findByRole('region', { name: 'hr' });
    await user.click(within(hrCard).getByRole('checkbox', { name: 'ben@acme.example' }));

    await waitFor(() => expect(calls.find((c) => c.method === 'PUT' && c.url === '/api/portal/groups/g1/members')?.body).toEqual({ userIds: ['u2', 'u3'] }));
  });

  it('previews which online tools a new grant allows and sends patterns split by comma', async () => {
    const calls = mockFetch(scripted((call) => (call.method === 'POST' ? { status: 201, body: { id: 'x9', serverPattern: 'hr', toolPatterns: ['list_*', 'sal*'] } } : undefined)));
    const user = userEvent.setup();
    renderPage();

    const hrCard = await screen.findByRole('region', { name: 'hr' });
    const forms = hrCard.querySelectorAll('form.grant-form');
    const addForm = forms[forms.length - 1];
    if (!(addForm instanceof HTMLElement)) throw new Error('add form missing');
    const server = within(addForm).getByLabelText('Server');
    const tools = within(addForm).getByLabelText('Tools');
    await user.clear(server);
    await user.type(server, 'HR');
    await user.clear(tools);
    await user.type(tools, 'list_*, sal*');

    await waitFor(() => expect(within(addForm).getByTestId('grant-preview')).toHaveTextContent('Allows 2 tool(s) that are online now: hr/list_staff, hr/salaries'));
    await user.click(within(addForm).getByRole('button', { name: 'Add grant' }));

    await waitFor(() => expect(calls.find((c) => c.method === 'POST' && c.url === '/api/portal/groups/g1/grants')?.body).toEqual({ serverPattern: 'HR', toolPatterns: ['list_*', 'sal*'] }));
  });

  it('blocks invalid tool patterns before they are sent', async () => {
    const calls = mockFetch(scripted());
    const user = userEvent.setup();
    renderPage();

    const hrCard = await screen.findByRole('region', { name: 'hr' });
    const forms = hrCard.querySelectorAll('form.grant-form');
    const addForm = forms[forms.length - 1];
    if (!(addForm instanceof HTMLElement)) throw new Error('add form missing');
    const tools = within(addForm).getByLabelText('Tools');
    await user.clear(tools);
    await user.type(tools, 'a b');

    expect(within(addForm).getByRole('alert')).toHaveTextContent('Not a valid tool pattern: a b');
    expect(within(addForm).getByRole('button', { name: 'Add grant' })).toBeDisabled();
    expect(calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('removes a grant and deletes a group after confirmation', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    const calls = mockFetch(scripted((call) => (call.method === 'DELETE' ? { status: 204 } : undefined)));
    const user = userEvent.setup();
    renderPage();

    const hrCard = await screen.findByRole('region', { name: 'hr' });
    await user.click(within(hrCard).getByRole('button', { name: 'Remove' }));
    await user.click(within(hrCard).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/grants/x1')).toBe(true));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.url === '/api/portal/groups/g1')).toBe(true));
  });

  it('creates a group and shows the server refusal for a taken name', async () => {
    mockFetch(scripted((call) => (call.method === 'POST' ? { status: 400, body: { errors: ['A group with this name already exists.'] } } : undefined)));
    const user = userEvent.setup();
    renderPage();

    await user.type(await screen.findByLabelText('Group name'), 'HR');
    await user.click(screen.getByRole('button', { name: 'Create group' }));

    expect(await screen.findByText('A group with this name already exists.')).toBeInTheDocument();
  });
});
