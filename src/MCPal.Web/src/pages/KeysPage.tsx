import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { ApiKey, CreatedApiKey, KeyPurpose } from '../api/types';
import { useMe } from '../components/useMe';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { Tag, type TagTone } from '../components/Tag';
import { t, type MessageKey } from '../i18n';

const keysQueryKey = ['keys'] as const;

const purposes: KeyPurpose[] = ['client', 'agent', 'any'];

/** Key purposes wear the cable colours: orange for the agent side, blue for Claude, both for legacy keys. */
const purposeTone: Record<KeyPurpose, TagTone> = { agent: 'agent', client: 'claude', any: 'any' };

/** The key works until the end of the chosen local day. */
function endOfDay(date: string): string {
  return new Date(`${date}T23:59:59`).toISOString();
}

function parseServers(text: string): string[] {
  return text
    .split(',')
    .map((server) => server.trim())
    .filter((server) => server.length > 0);
}

function servers(key: ApiKey): string {
  return key.allowedServers.length === 0 ? t('keys.allServers') : key.allowedServers.join(', ');
}

function formatDate(value: string | null): string {
  return value === null ? t('keys.never') : new Date(value).toLocaleString();
}

export function KeysPage() {
  const [name, setName] = useState('');
  const [purpose, setPurpose] = useState<KeyPurpose>('client');
  const [serversText, setServersText] = useState('');
  const [expiresOn, setExpiresOn] = useState('');
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const queryClient = useQueryClient();
  const keys = useQuery({ queryKey: keysQueryKey, queryFn: api.keys });
  const me = useMe();
  // Members create keys for themselves to use Claude; agent keys and legacy 'any' keys are for owners.
  const allowedPurposes = me.data?.role === 'member' ? purposes.filter((option) => option === 'client') : purposes;
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections });
  const suggestions = [...new Set(connections.data?.flatMap((connection) => connection.servers.map((server) => server.name)) ?? [])];
  const showCreator = me.data?.role !== 'member';
  const create = useMutation({
    mutationFn: () =>
      api.createKey(name, {
        purpose,
        allowedServers: purpose === 'agent' ? [] : parseServers(serversText),
        ...(expiresOn === '' ? {} : { expiresAt: endOfDay(expiresOn) }),
      }),
    onSuccess: async (key) => {
      setCreated(key);
      setName('');
      setServersText('');
      setExpiresOn('');
      await queryClient.invalidateQueries({ queryKey: keysQueryKey });
    },
  });
  const revoke = useMutation({
    mutationFn: (id: string) => api.revokeKey(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keysQueryKey }),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    create.mutate();
  }

  return (
    <>
      <h1>{t('keys.title')}</h1>
      <p className="muted">{t('keys.intro')}</p>

      {created !== null && (
        <section className="notice hazard" aria-live="polite">
          <h2>{t('keys.createdTitle')}</h2>
          <p>{t('keys.createdBody')}</p>
          <div className="row secret">
            <code data-testid="created-key">{created.key}</code>
            <CopyButton value={created.key} />
          </div>
        </section>
      )}

      <section className="panel">
        <form className="row" onSubmit={submit}>
          <label className="grow">
            {t('keys.name')}
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('keys.namePlaceholder')} required />
          </label>
          <label>
            {t('keys.purpose')}
            <select value={purpose} onChange={(e) => setPurpose(e.target.value as KeyPurpose)}>
              {allowedPurposes.map((option) => (
                <option key={option} value={option}>
                  {t(`keys.purpose.${option}` satisfies MessageKey)}
                </option>
              ))}
            </select>
          </label>
          {purpose !== 'agent' && (
            <label className="grow">
              {t('keys.servers')}
              <input
                value={serversText}
                onChange={(e) => setServersText(e.target.value)}
                placeholder={t('keys.serversPlaceholder')}
                list="known-servers"
                autoComplete="off"
              />
              <datalist id="known-servers">
                {suggestions.map((server) => (
                  <option key={server} value={server} />
                ))}
              </datalist>
            </label>
          )}
          <label>
            {t('keys.expires')}
            <input type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
          </label>
          <button type="submit" disabled={create.isPending}>
            {t('keys.create')}
          </button>
        </form>
        <p className="muted">{t(`keys.purpose.${purpose}.help` satisfies MessageKey)}</p>
        {purpose !== 'agent' && <p className="muted">{t('keys.serversHelp')}</p>}
      </section>
      <ErrorText error={create.error ?? revoke.error} />

      {keys.data?.length === 0 && <p className="muted">{t('keys.empty')}</p>}
      {keys.data !== undefined && keys.data.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('keys.col.name')}</th>
                <th>{t('keys.col.prefix')}</th>
                <th>{t('keys.col.purpose')}</th>
                <th>{t('keys.col.servers')}</th>
                <th>{t('keys.col.created')}</th>
                {showCreator && <th>{t('keys.col.createdBy')}</th>}
                <th>{t('keys.col.expires')}</th>
                <th>{t('keys.col.lastUsed')}</th>
                <th>{t('keys.col.status')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {keys.data.map((key) => (
                <tr key={key.id} className={key.disabled ? 'is-off' : undefined}>
                  <td>{key.name}</td>
                  <td>
                    <code>{key.prefix}…</code>
                  </td>
                  <td>
                    <Tag tone={purposeTone[key.purpose]}>{t(`keys.purposeShort.${key.purpose}` satisfies MessageKey)}</Tag>
                  </td>
                  <td>{key.purpose === 'agent' ? '' : servers(key)}</td>
                  <td className="when">{formatDate(key.createdAt)}</td>
                  {showCreator && <td>{key.createdBy ?? ''}</td>}
                  <td className="when">{key.expiresAt === null ? t('keys.never') : formatDate(key.expiresAt)}</td>
                  <td className="when">{formatDate(key.lastUsedAt)}</td>
                  <td>{key.disabled ? <Tag tone="off">{t('keys.status.revoked')}</Tag> : <Tag tone="ok">{t('keys.status.active')}</Tag>}</td>
                  <td>
                    {!key.disabled && (
                      <button
                        type="button"
                        className="danger small"
                        onClick={() => {
                          if (window.confirm(t('keys.revokeConfirm'))) {
                            revoke.mutate(key.id);
                          }
                        }}
                      >
                        {t('keys.revoke')}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
