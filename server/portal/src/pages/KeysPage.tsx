import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { CreatedApiKey, KeyPurpose } from '../api/types';
import { useMe } from '../components/useMe';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { Tag, type TagTone } from '../components/Tag';
import { t, type MessageKey } from '../i18n';

const keysQueryKey = ['keys'] as const;

const purposes: KeyPurpose[] = ['personal', 'bridge'];

/** Key purposes wear the cable colours: orange for the bridge side, blue for Claude. */
const purposeTone: Record<KeyPurpose, TagTone> = { bridge: 'bridge', personal: 'claude' };

/** The key works until the end of the chosen local day. */
function endOfDay(date: string): string {
  return new Date(`${date}T23:59:59`).toISOString();
}

function formatDate(value: string | null): string {
  return value === null ? t('keys.never') : new Date(value).toLocaleString();
}

export function KeysPage() {
  const [name, setName] = useState('');
  const [purpose, setPurpose] = useState<KeyPurpose>('personal');
  const [expiresOn, setExpiresOn] = useState('');
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const queryClient = useQueryClient();
  const keys = useQuery({ queryKey: keysQueryKey, queryFn: api.keys });
  const me = useMe();
  // Members create personal access tokens for themselves; bridge keys are for owners.
  const allowedPurposes = me.data?.role === 'member' ? purposes.filter((option) => option === 'personal') : purposes;
  const isOwner = me.data?.role !== 'member';
  const create = useMutation({
    mutationFn: () =>
      api.createKey(name, {
        purpose,
        ...(expiresOn === '' ? {} : { expiresAt: endOfDay(expiresOn) }),
      }),
    onSuccess: async (key) => {
      setCreated(key);
      setName('');
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
          <label>
            {t('keys.expires')}
            <input type="date" value={expiresOn} onChange={(e) => setExpiresOn(e.target.value)} />
          </label>
          <button type="submit" disabled={create.isPending}>
            {t('keys.create')}
          </button>
        </form>
        <p className="muted">{t(`keys.purpose.${purpose}.help` satisfies MessageKey)}</p>
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
                <th>{t('keys.col.created')}</th>
                {isOwner && <th>{t('keys.col.user')}</th>}
                {isOwner && <th>{t('keys.col.createdBy')}</th>}
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
                  <td className="when">{formatDate(key.createdAt)}</td>
                  {isOwner && <td>{key.userEmail ?? ''}</td>}
                  {isOwner && <td>{key.createdBy ?? ''}</td>}
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
