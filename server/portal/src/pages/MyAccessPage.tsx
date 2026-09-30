import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type FormEvent } from 'react';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { Tag } from '../components/Tag';
import { meQueryKey, useMe } from '../components/useMe';
import { t, type MessageKey } from '../i18n';

/** What the signed-in user may use right now, and their display name (passed to local MCP servers). */
export function MyAccessPage() {
  const queryClient = useQueryClient();
  const me = useMe();
  const access = useQuery({ queryKey: ['my-access'], queryFn: api.myAccess, refetchInterval: 10000 });
  const [displayName, setDisplayName] = useState('');
  useEffect(() => {
    setDisplayName(me.data?.displayName ?? '');
  }, [me.data?.displayName]);
  const save = useMutation({
    mutationFn: () => api.updateMe(displayName),
    onSuccess: (updated) => queryClient.setQueryData(meQueryKey, updated),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  const data = access.data;
  return (
    <>
      <h1>{t('access.title')}</h1>
      <p className="muted">{t('access.intro')}</p>

      <section className="panel">
        <h2>{t('access.profile')}</h2>
        <form className="row" onSubmit={submit}>
          <label className="grow">
            {t('access.displayName')}
            <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} placeholder={me.data?.email ?? ''} maxLength={200} />
          </label>
          <button type="submit" disabled={save.isPending}>
            {t('access.save')}
          </button>
        </form>
        <p className="muted">{t('access.displayNameHelp')}</p>
        <ErrorText error={save.error} />
      </section>

      <ErrorText error={access.error} />
      {data !== undefined && (
        <section className="panel">
          <h2>{t('access.effective')}</h2>
          <p>
            <Tag tone={data.role === 'owner' ? 'ink' : 'plain'}>{t(`users.role.${data.role}` satisfies MessageKey)}</Tag>{' '}
            {data.groups.map((group) => (
              <Tag key={group} tone="claude">
                {group}
              </Tag>
            ))}
          </p>
          {data.allTools && <p className="muted">{t('access.ownerAll')}</p>}
          {data.tools.length === 0 ? (
            <p className="empty">{t('access.noTools')}</p>
          ) : (
            <ul className="tools">
              {data.tools.map((tool) => (
                <li key={tool.publicName}>
                  <code>{tool.publicName}</code>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </>
  );
}
