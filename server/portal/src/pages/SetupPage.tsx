import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { CreatedApiKey, Setup } from '../api/types';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { t, type MessageKey } from '../i18n';

const platforms = ['linux-x64', 'linux-arm64', 'win-x64'] as const;
type Platform = (typeof platforms)[number];

const defaultKeyName = 'Main bridge';

function Snippet({ value }: { value: string }) {
  return (
    <div className="snippet">
      <code>{value}</code>
      <CopyButton value={value} />
    </div>
  );
}

/** The release archive for a platform; a placeholder version when the server does not know the newest release. */
function archiveOf(setup: Setup, platform: Platform) {
  const known = setup.downloads.find((d) => d.rid === platform);
  const extension = platform === 'win-x64' ? 'zip' : 'tar.gz';
  const fileName = known?.fileName ?? `mcpal-bridge-<version>-${platform}.${extension}`;
  return { fileName, folder: fileName.slice(0, -(extension.length + 1)), url: known?.url };
}

/** The install commands of the release archive. The key is part of the command, not of mcpal.json. */
function installCommands(setup: Setup, platform: Platform, key: string): string[] {
  const { fileName, folder } = archiveOf(setup, platform);
  if (platform === 'win-x64') {
    return [
      `Expand-Archive ${fileName} .`,
      `cd ${folder}`,
      'New-Item -Force -ItemType Directory $env:ProgramData\\MCPal | Out-Null',
      'Copy-Item ..\\mcpal.json, ..\\mcp.json $env:ProgramData\\MCPal\\',
      `.\\install.ps1 -ApiKey ${key}`,
    ];
  }
  return [
    `tar xzf ${fileName}`,
    `cd ${folder}`,
    'sudo install -D -m 640 ../mcpal.json /etc/mcpal/mcpal.json',
    'sudo install -m 640 ../mcp.json /etc/mcpal/mcp.json',
    `sudo ./install.sh --api-key ${key}`,
  ];
}

function configHref(json: string): string {
  return `data:application/json;charset=utf-8,${encodeURIComponent(json)}`;
}

export function SetupPage() {
  const [platform, setPlatform] = useState<Platform>('linux-x64');
  const [keyName, setKeyName] = useState(defaultKeyName);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const queryClient = useQueryClient();
  const setup = useQuery({ queryKey: ['setup'], queryFn: api.setup });
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections, refetchInterval: 3000 });
  const createKey = useMutation({
    mutationFn: () => api.createKey(keyName, { purpose: 'bridge' }),
    onSuccess: async (key) => {
      setCreated(key);
      await queryClient.invalidateQueries({ queryKey: ['keys'] });
    },
  });

  function submitKey(event: FormEvent) {
    event.preventDefault();
    createKey.mutate();
  }

  const online = connections.data?.[0];
  const data = setup.data;

  return (
    <>
      <h1>{t('setup.title')}</h1>
      <p className="muted">{t('setup.intro')}</p>
      <ErrorText error={setup.error} />
      {data !== undefined && (
        <ol className="steps">
          <li>
            <h2>{t('setup.download.title')}</h2>
            <div className="step-body">
              <fieldset className="choice">
                <legend>{t('setup.platform')}</legend>
                {platforms.map((option) => (
                  <label key={option}>
                    <input type="radio" name="platform" checked={platform === option} onChange={() => setPlatform(option)} />
                    {t(`setup.platform.${option}` satisfies MessageKey)}
                  </label>
                ))}
              </fieldset>
              {archiveOf(data, platform).url === undefined ? (
                <p>
                  {t('setup.download.fallback')} <a href={data.releasesUrl}>{t('setup.download.releases')}</a>
                </p>
              ) : (
                <p>
                  <a href={archiveOf(data, platform).url}>{t('setup.download.link', { file: archiveOf(data, platform).fileName })}</a>
                  {data.checksumsUrl !== null && (
                    <>
                      {' · '}
                      <a href={data.checksumsUrl}>{t('setup.download.checksums')}</a>
                    </>
                  )}
                </p>
              )}
            </div>
          </li>
          <li>
            <h2>{t('setup.key.title')}</h2>
            <div className="step-body">
              <p>{t('setup.key.body')}</p>
              {created === null ? (
                <form className="row" onSubmit={submitKey}>
                  <label className="grow">
                    {t('setup.key.name')}
                    <input value={keyName} onChange={(event) => setKeyName(event.target.value)} required />
                  </label>
                  <button type="submit" disabled={createKey.isPending}>
                    {t('setup.key.create')}
                  </button>
                </form>
              ) : (
                <section className="notice hazard" aria-live="polite">
                  <p>{t('keys.createdBody')}</p>
                  <div className="row secret">
                    <code data-testid="created-key">{created.key}</code>
                    <CopyButton value={created.key} />
                  </div>
                </section>
              )}
              <ErrorText error={createKey.error} />
            </div>
          </li>
          <li>
            <h2>{t('setup.config.title')}</h2>
            <div className="step-body">
              <p>{t('setup.config.body')}</p>
              <a className="button-link" href={configHref(data.configJson)} download="mcpal.json">
                {t('setup.config.download')}
              </a>
              <p className="muted">{t('setup.config.servers')}</p>
              <p>
                {t('setup.config.sample')}{' '}
                <a href={configHref(data.sampleMcpJson)} download="mcp.json">
                  {t('setup.config.sampleDownload')}
                </a>
              </p>
            </div>
          </li>
          <li>
            <h2>{t('setup.install.title')}</h2>
            <div className="step-body">
              <p>{t('setup.install.body')}</p>
              {installCommands(data, platform, created?.key ?? t('setup.key.placeholder')).map((command) => (
                <Snippet key={command} value={command} />
              ))}
            </div>
          </li>
          <li>
            <h2>{t('setup.verify.title')}</h2>
            <div className="step-body" aria-live="polite">
              {online === undefined ? (
                <p>{t('setup.verify.waiting')}</p>
              ) : (
                <>
                  <p>{t('setup.verify.online', { name: online.bridgeName })}</p>
                  <p>
                    {online.servers.length === 0
                      ? t('setup.verify.noServers')
                      : t('setup.verify.servers', { servers: online.servers.map((server) => server.name).join(', ') })}
                  </p>
                  <Link to="/connect">{t('setup.verify.next')}</Link>
                </>
              )}
            </div>
          </li>
        </ol>
      )}
    </>
  );
}
