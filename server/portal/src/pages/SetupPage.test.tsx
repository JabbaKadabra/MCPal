import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import type { Connection, Setup } from '../api/types';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { SetupPage } from './SetupPage';

const releases = 'https://github.com/o/r/releases';
const bridgeKey = 'mcpal_0123abcd_' + 'A'.repeat(40);

const setup: Setup = {
  mcpalUrl: 'https://mcpal.example.com',
  bridgeVersion: '1.2.3',
  downloads: [
    { rid: 'linux-x64', os: 'linux', fileName: 'mcpal-bridge-1.2.3-linux-x64.tar.gz', url: `${releases}/download/v1.2.3/mcpal-bridge-1.2.3-linux-x64.tar.gz` },
    { rid: 'linux-arm64', os: 'linux', fileName: 'mcpal-bridge-1.2.3-linux-arm64.tar.gz', url: `${releases}/download/v1.2.3/mcpal-bridge-1.2.3-linux-arm64.tar.gz` },
    { rid: 'win-x64', os: 'windows', fileName: 'mcpal-bridge-1.2.3-win-x64.zip', url: `${releases}/download/v1.2.3/mcpal-bridge-1.2.3-win-x64.zip` },
  ],
  releasesUrl: `${releases}/tag/v1.2.3`,
  checksumsUrl: `${releases}/download/v1.2.3/sha256sums.txt`,
  configJson: JSON.stringify({ mcpal: { url: 'https://mcpal.example.com' } }),
  sampleMcpJson: JSON.stringify({ mcpServers: { everything: { command: 'npx', includeTools: ['echo'] } } }),
  hasConnectedBridge: false,
};

const online: Connection = {
  bridgeName: 'hq-01',
  bridgeVersion: '1.2.3.0',
  updateAvailable: false,
  latestBridgeVersion: null,
  connectedAt: '2026-01-01T10:00:00Z',
  apiKeyName: 'Main bridge',
  supportsUserContext: true,
  servers: [{ name: 'kb', tools: ['search'], audience: 'mcpal:acme/kb' }],
  rejected: [],
  rejectedTools: [],
};

function backend(options: { setup?: Setup; connections?: Connection[] } = {}) {
  return (call: RecordedCall) => {
    if (call.url === '/api/portal/setup') return { body: options.setup ?? setup };
    if (call.url === '/api/portal/connections') return { body: options.connections ?? [] };
    if (call.url === '/api/portal/csrf') return { body: { token: 't' } };
    if (call.url === '/api/portal/keys' && call.method === 'POST') {
      return { status: 201, body: { id: 'k1', name: 'Main bridge', prefix: 'mcpal_0123abcd', createdAt: '2026-01-01T10:00:00Z', expiresAt: null, key: bridgeKey, purpose: 'bridge' } };
    }
    return undefined;
  };
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <SetupPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('SetupPage', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('links the archive of the chosen platform and the checksums', async () => {
    mockFetch(backend());
    renderPage();

    expect(await screen.findByRole('link', { name: /mcpal-bridge-1\.2\.3-linux-x64\.tar\.gz/ })).toHaveAttribute('href', setup.downloads[0]?.url);
    await userEvent.click(screen.getByRole('radio', { name: 'Windows x64' }));
    expect(screen.getByRole('link', { name: /mcpal-bridge-1\.2\.3-win-x64\.zip/ })).toHaveAttribute('href', setup.downloads[2]?.url);
    expect(screen.getByRole('link', { name: 'SHA-256 checksums' })).toHaveAttribute('href', setup.checksumsUrl);
  });

  it('points to the latest release page when the version is unknown', async () => {
    mockFetch(backend({ setup: { ...setup, bridgeVersion: null, downloads: [], checksumsUrl: null, releasesUrl: `${releases}/latest` } }));
    renderPage();

    expect(await screen.findByRole('link', { name: 'Releases page' })).toHaveAttribute('href', `${releases}/latest`);
    expect(screen.queryByRole('link', { name: /mcpal-bridge-/ })).not.toBeInTheDocument();
  });

  it('creates a bridge key, shows it once and puts it into the install command', async () => {
    const calls = mockFetch(backend());
    renderPage();
    expect(await screen.findByText('sudo ./install.sh --api-key <your-bridge-key>')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Create bridge key' }));

    expect(await screen.findByTestId('created-key')).toHaveTextContent(bridgeKey);
    expect(screen.getByText(`sudo ./install.sh --api-key ${bridgeKey}`)).toBeInTheDocument();
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/portal/keys');
    expect(post?.body).toMatchObject({ name: 'Main bridge', purpose: 'bridge' });
  });

  it('uses the Windows commands for the Windows platform', async () => {
    mockFetch(backend());
    renderPage();
    await userEvent.click(await screen.findByRole('radio', { name: 'Windows x64' }));

    expect(screen.getByText('.\\install.ps1 -ApiKey <your-bridge-key>')).toBeInTheDocument();
    expect(screen.getByText('Expand-Archive mcpal-bridge-1.2.3-win-x64.zip .')).toBeInTheDocument();
  });

  it('offers the pre-filled mcpal.json for download and explains mcp.json', async () => {
    mockFetch(backend());
    renderPage();

    const link = await screen.findByRole('link', { name: 'Download mcpal.json' });
    expect(link).toHaveAttribute('download', 'mcpal.json');
    expect(decodeURIComponent((link.getAttribute('href') ?? '').split(',')[1] ?? '')).toBe(setup.configJson);
    expect(screen.getByText(/For mcp\.json copy your existing MCP config/)).toBeInTheDocument();
  });

  it('offers a sample mcp.json with an echo server', async () => {
    mockFetch(backend());
    renderPage();

    const link = await screen.findByRole('link', { name: 'Download sample mcp.json' });
    expect(link).toHaveAttribute('download', 'mcp.json');
    expect(decodeURIComponent((link.getAttribute('href') ?? '').split(',')[1] ?? '')).toBe(setup.sampleMcpJson);
  });

  it('installs mcpal.json and mcp.json together', async () => {
    mockFetch(backend());
    renderPage();

    expect(await screen.findByText('sudo install -D -m 640 ../mcpal.json /etc/mcpal/mcpal.json')).toBeInTheDocument();
    expect(screen.getByText('sudo install -m 640 ../mcp.json /etc/mcpal/mcp.json')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('radio', { name: 'Windows x64' }));
    expect(screen.getByText('Copy-Item ..\\mcpal.json, ..\\mcp.json $env:ProgramData\\MCPal\\')).toBeInTheDocument();
  });

  it('waits for a bridge and then reports it online', async () => {
    mockFetch(backend());
    renderPage();
    expect(await screen.findByText('Waiting for the bridge to connect…')).toBeInTheDocument();
  });

  it('reports a connected bridge with its servers and links to connecting Claude', async () => {
    mockFetch(backend({ connections: [online] }));
    renderPage();

    expect(await screen.findByText('Bridge hq-01 is online.')).toBeInTheDocument();
    expect(screen.getByText('Servers: kb')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Next: connect Claude' })).toHaveAttribute('href', '/connect');
  });
});
