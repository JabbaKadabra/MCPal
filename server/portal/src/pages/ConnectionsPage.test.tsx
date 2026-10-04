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
  bridgeName: 'hq-01',
  bridgeVersion: '1.0.0.0',
  updateAvailable: false,
  latestBridgeVersion: null,
  connectedAt: '2026-01-01T10:00:00Z',
  apiKeyName: 'HQ bridge',
  supportsUserContext: true,
  servers: [{ name: 'files_v2', tools: ['write'], audience: 'mcpal:acme/files_v2' }],
  rejected: [{ server: 'kb', reason: 'Server name is already registered by another bridge connection of this company.' }],
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

  it('shows the token audience of each server and that the bridge passes the caller', async () => {
    mockFetch(() => ({ body: [connection] }));

    renderPage();

    expect(await screen.findByText('mcpal:acme/files_v2')).toBeInTheDocument();
    expect(screen.getByText('passes the caller')).toBeInTheDocument();
    expect(screen.queryByText(/older than protocol 1\.2/)).not.toBeInTheDocument();
  });

  it('warns when a bridge is too old to pass the caller', async () => {
    mockFetch(() => ({ body: [{ ...connection, supportsUserContext: false }] }));

    renderPage();

    expect(await screen.findByText('no caller')).toBeInTheDocument();
    expect(screen.getByText(/older than protocol 1\.2/)).toBeInTheDocument();
  });

  it('shows the bridge version', async () => {
    mockFetch(() => ({ body: [connection] }));

    renderPage();

    expect(await screen.findByText(/bridge 1\.0\.0\.0/)).toBeInTheDocument();
    expect(screen.queryByText(/newer bridge/)).not.toBeInTheDocument();
  });

  it('points out that a newer bridge is available', async () => {
    mockFetch(() => ({ body: [{ ...connection, updateAvailable: true, latestBridgeVersion: '1.2.0' }] }));

    renderPage();

    expect(await screen.findByText(/A newer bridge \(1\.2\.0\) is available/)).toBeInTheDocument();
  });
});
