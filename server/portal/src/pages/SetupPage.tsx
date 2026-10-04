import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { CreatedApiKey, Enrollment, Setup } from '../api/types';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { t, type MessageKey } from '../i18n';

/** How the bridge is installed: as a container (default) or from the release archive of a platform. */
const methods = ['docker', 'linux-x64', 'linux-arm64', 'win-x64'] as const;
type Method = (typeof methods)[number];
type Platform = Exclude<Method, 'docker'>;

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

/** What the bridge signs in with: a one-time enrollment code (default) or a bridge key the owner created. */
type Credential = { kind: 'enroll' | 'key'; value: string };

/** The container needs no mcpal.json: the server URL and the credential are environment variables. */
function dockerCommands(setup: Setup, credential: Credential): string[] {
  const variable = credential.kind === 'enroll' ? 'MCPAL_ENROLL' : 'MCPAL_API_KEY';
  return [
    `docker run -d --name mcpal-bridge --hostname mcpal-bridge --restart unless-stopped -e MCPAL_URL=${setup.mcpalUrl} -e ${variable}=${credential.value} -v "$PWD/mcp.json:/config/mcp.json:ro" -v mcpal-bridge-data:/data ${setup.imageReference}`,
    'docker logs -f mcpal-bridge',
  ];
}

/** The install commands of the release archive. The credential is part of the command, not of mcpal.json. */
function installCommands(setup: Setup, platform: Platform, credential: Credential): string[] {
  const { fileName, folder } = archiveOf(setup, platform);
  if (platform === 'win-x64') {
    return [
      `Expand-Archive ${fileName} .`,
      `cd ${folder}`,
      'New-Item -Force -ItemType Directory $env:ProgramData\\MCPal | Out-Null',
      'Copy-Item ..\\mcpal.json, ..\\mcp.json $env:ProgramData\\MCPal\\',
      `.\\install.ps1 ${credential.kind === 'enroll' ? '-Enroll' : '-ApiKey'} ${credential.value}`,
    ];
  }
  return [
    `tar xzf ${fileName}`,
    `cd ${folder}`,
    'sudo install -D -m 640 ../mcpal.json /etc/mcpal/mcpal.json',
    'sudo install -m 640 ../mcp.json /etc/mcpal/mcp.json',
    `sudo ./install.sh ${credential.kind === 'enroll' ? '--enroll' : '--api-key'} ${credential.value}`,
  ];
}

/** Seconds from now to the given time, never below zero. */
function secondsLeft(expiresAt: string, now: number): number {
  return Math.max(0, Math.floor((Date.parse(expiresAt) - now) / 1000));
}

function clock(seconds: number): string {
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

function configHref(json: string): string {
  return `data:application/json;charset=utf-8,${encodeURIComponent(json)}`;
}

export function SetupPage() {
  const [method, setMethod] = useState<Method>('docker');
  const [keyName, setKeyName] = useState(defaultKeyName);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const [enrollment, setEnrollment] = useState<Enrollment | null>(null);
  const [showKey, setShowKey] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const queryClient = useQueryClient();
  const setup = useQuery({ queryKey: ['setup'], queryFn: api.setup });
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections, refetchInterval: 3000 });
  const createEnrollment = useMutation({
    mutationFn: api.createEnrollment,
    onSuccess: (result) => {
      setEnrollment(result);
      setNow(Date.now());
    },
  });
  const createKey = useMutation({
    mutationFn: () => api.createKey(keyName, { purpose: 'bridge' }),
    onSuccess: async (key) => {
      setCreated(key);
      await queryClient.invalidateQueries({ queryKey: ['keys'] });
    },
  });

  useEffect(() => {
    if (enrollment === null) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [enrollment]);

  function submitKey(event: FormEvent) {
    event.preventDefault();
    createKey.mutate();
  }

  const remaining = enrollment === null ? null : secondsLeft(enrollment.expiresAt, now);
  const expired = remaining === 0;
  // A created key wins (the owner chose the advanced path); an expired code never stays in a command someone might paste.
  const credential: Credential =
    created !== null
      ? { kind: 'key', value: created.key }
      : { kind: 'enroll', value: enrollment !== null && !expired ? enrollment.code : t('setup.enroll.placeholder') };

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
            <h2>{t(method === 'docker' ? 'setup.image.title' : 'setup.download.title')}</h2>
            <div className="step-body">
              <fieldset className="choice">
                <legend>{t('setup.method')}</legend>
                {methods.map((option) => (
                  <label key={option}>
                    <input type="radio" name="method" checked={method === option} onChange={() => setMethod(option)} />
                    {t(`setup.method.${option}` satisfies MessageKey)}
                  </label>
                ))}
              </fieldset>
              {method === 'docker' ? (
                <>
                  <p>{t('setup.image.body')}</p>
                  <Snippet value={data.imageReference} />
                </>
              ) : archiveOf(data, method).url === undefined ? (
                <p>
                  {t('setup.download.fallback')} <a href={data.releasesUrl}>{t('setup.download.releases')}</a>
                </p>
              ) : (
                <p>
                  <a href={archiveOf(data, method).url}>{t('setup.download.link', { file: archiveOf(data, method).fileName })}</a>
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
            <h2>{t('setup.enroll.title')}</h2>
            <div className="step-body">
              <p>{t('setup.enroll.body')}</p>
              <div className="row">
                <button type="button" disabled={createEnrollment.isPending} onClick={() => createEnrollment.mutate()}>
                  {t(enrollment === null ? 'setup.enroll.generate' : 'setup.enroll.again')}
                </button>
                {remaining !== null && (
                  <span className={expired ? 'muted' : undefined} aria-live="polite">
                    {expired ? t('setup.enroll.expired') : t('setup.enroll.valid', { time: clock(remaining) })}
                  </span>
                )}
              </div>
              <ErrorText error={createEnrollment.error} />
              <button type="button" className="ghost small" onClick={() => setShowKey((shown) => !shown)}>
                {t('setup.key.toggle')}
              </button>
              {showKey && (
                <div>
                  <h3>{t('setup.key.title')}</h3>
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
              )}
            </div>
          </li>
          <li>
            <h2>{t('setup.config.title')}</h2>
            <div className="step-body">
              <p>{t(method === 'docker' ? 'setup.config.dockerBody' : 'setup.config.body')}</p>
              {method !== 'docker' && (
                <a className="button-link" href={configHref(data.configJson)} download="mcpal.json">
                  {t('setup.config.download')}
                </a>
              )}
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
              <p>{t(method === 'docker' ? 'setup.install.dockerBody' : 'setup.install.body')}</p>
              {(method === 'docker' ? dockerCommands(data, credential) : installCommands(data, method, credential)).map((command) => (
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
