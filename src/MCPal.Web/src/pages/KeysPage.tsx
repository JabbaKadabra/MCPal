import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { CreatedApiKey } from '../api/types';
import { CopyButton } from '../components/CopyButton';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

const keysQueryKey = ['keys'] as const;

function formatDate(value: string | null): string {
  return value === null ? t('keys.never') : new Date(value).toLocaleString();
}

export function KeysPage() {
  const [name, setName] = useState('');
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const queryClient = useQueryClient();
  const keys = useQuery({ queryKey: keysQueryKey, queryFn: api.keys });
  const create = useMutation({
    mutationFn: () => api.createKey(name),
    onSuccess: async (key) => {
      setCreated(key);
      setName('');
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
        <section className="notice" aria-live="polite">
          <h2>{t('keys.createdTitle')}</h2>
          <p>{t('keys.createdBody')}</p>
          <div className="row">
            <code data-testid="created-key">{created.key}</code>
            <CopyButton value={created.key} />
          </div>
        </section>
      )}

      <form className="row" onSubmit={submit}>
        <label className="grow">
          {t('keys.name')}
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('keys.namePlaceholder')} required />
        </label>
        <button type="submit" disabled={create.isPending}>
          {t('keys.create')}
        </button>
      </form>
      <ErrorText error={create.error ?? revoke.error} />

      {keys.data?.length === 0 && <p className="muted">{t('keys.empty')}</p>}
      {keys.data !== undefined && keys.data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('keys.col.name')}</th>
              <th>{t('keys.col.prefix')}</th>
              <th>{t('keys.col.created')}</th>
              <th>{t('keys.col.lastUsed')}</th>
              <th>{t('keys.col.status')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {keys.data.map((key) => (
              <tr key={key.id}>
                <td>{key.name}</td>
                <td>
                  <code>{key.prefix}…</code>
                </td>
                <td>{formatDate(key.createdAt)}</td>
                <td>{formatDate(key.lastUsedAt)}</td>
                <td>{key.disabled ? t('keys.status.revoked') : t('keys.status.active')}</td>
                <td>
                  {!key.disabled && (
                    <button
                      type="button"
                      className="danger"
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
      )}
    </>
  );
}
