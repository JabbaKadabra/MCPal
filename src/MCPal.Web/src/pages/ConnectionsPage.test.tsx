import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Connection } from '../api/types';
import { mockFetch } from '../test/fetchMock';
import { ConnectionsPage } from './ConnectionsPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ConnectionsPage />
    </QueryClientProvider>,
  );
}

const connection: Connection = {
  agentName: 'hq-01',
  connectedAt: '2026-01-01T10:00:00Z',
  apiKeyName: 'HQ agent',
  servers: [{ name: 'files_v2', tools: ['write'] }],
  rejected: [{ server: 'kb', reason: 'Server name is already registered by another agent connection of this company.' }],
  rejectedTools: [{ server: 'files_v2', tool: 'read', reason: "Public tool name 'files_v2__read' is already used." }],
};

describe('ConnectionsPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows rejected servers and rejected tools with their reasons', async () => {
    mockFetch(() => ({ body: [connection] }));

    renderPage();

    expect(await screen.findByText(/kb — Server name is already registered/)).toBeInTheDocument();
    expect(screen.getByText(/files_v2 \/ read — Public tool name 'files_v2__read' is already used\./)).toBeInTheDocument();
  });
});
