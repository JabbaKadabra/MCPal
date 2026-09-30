import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api, auditQuery } from '../api/client';
import type { AuditEntry, AuditFilters, AuditOutcome } from '../api/types';
import { ErrorText } from '../components/ErrorText';
import { t, type MessageKey } from '../i18n';

const outcomes: AuditOutcome[] = ['ok', 'tool_error', 'timeout', 'offline', 'relay_error', 'cancelled'];
const noFilters: AuditFilters = { tool: '', outcome: '', keyId: '', from: '', to: '' };

/** A datetime-local value is local time without offset; the API wants an instant. */
function toInstant(localValue: string): string {
  return localValue === '' ? '' : new Date(localValue).toISOString();
}

function caller(entry: AuditEntry): string {
  if (entry.authKind === 'oauth') {
    return entry.oauthClientId ?? t('audit.caller.oauth');
  }
  return entry.apiKeyName ?? entry.apiKeyId ?? '';
}

export function AuditPage() {
  const [draft, setDraft] = useState<AuditFilters>(noFilters);
  const [applied, setApplied] = useState<AuditFilters>(noFilters);
  const keys = useQuery({ queryKey: ['keys'], queryFn: api.keys });
  const audit = useInfiniteQuery({
    queryKey: ['audit', applied],
    queryFn: ({ pageParam }) => api.audit(applied, pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const entries = audit.data?.pages.flatMap((page) => page.items) ?? [];

  function submit(event: FormEvent) {
    event.preventDefault();
    setApplied({ ...draft, from: toInstant(draft.from), to: toInstant(draft.to) });
  }

  return (
    <>
      <h1>{t('audit.title')}</h1>
      <p className="muted">{t('audit.intro')}</p>

      <form className="row" onSubmit={submit}>
        <label>
          {t('audit.filter.tool')}
          <input value={draft.tool} onChange={(e) => setDraft({ ...draft, tool: e.target.value })} />
        </label>
        <label>
          {t('audit.filter.outcome')}
          <select value={draft.outcome} onChange={(e) => setDraft({ ...draft, outcome: e.target.value })}>
            <option value="">{t('audit.filter.any')}</option>
            {outcomes.map((outcome) => (
              <option key={outcome} value={outcome}>
                {t(`audit.outcome.${outcome}` satisfies MessageKey)}
              </option>
            ))}
          </select>
        </label>
        <label>
          {t('audit.filter.key')}
          <select value={draft.keyId} onChange={(e) => setDraft({ ...draft, keyId: e.target.value })}>
            <option value="">{t('audit.filter.any')}</option>
            {keys.data?.map((key) => (
              <option key={key.id} value={key.id}>
                {key.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          {t('audit.filter.from')}
          <input type="datetime-local" value={draft.from} onChange={(e) => setDraft({ ...draft, from: e.target.value })} />
        </label>
        <label>
          {t('audit.filter.to')}
          <input type="datetime-local" value={draft.to} onChange={(e) => setDraft({ ...draft, to: e.target.value })} />
        </label>
        <button type="submit">{t('audit.filter.apply')}</button>
        <a href={`/api/portal/audit/export.csv${auditQuery(applied)}`}>{t('audit.export')}</a>
      </form>

      <ErrorText error={audit.error} />
      {audit.isSuccess && entries.length === 0 && <p className="muted">{t('audit.empty')}</p>}
      {entries.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>{t('audit.col.time')}</th>
              <th>{t('audit.col.tool')}</th>
              <th>{t('audit.col.caller')}</th>
              <th>{t('audit.col.agent')}</th>
              <th>{t('audit.col.duration')}</th>
              <th>{t('audit.col.outcome')}</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((entry) => (
              <tr key={entry.id}>
                <td>{new Date(entry.occurredAt).toLocaleString()}</td>
                <td>
                  <code>{entry.publicName}</code>
                </td>
                <td>{caller(entry)}</td>
                <td>{entry.agentName}</td>
                <td>{`${entry.durationMs} ms`}</td>
                <td>
                  {t(`audit.outcome.${entry.outcome}` satisfies MessageKey)}
                  {entry.errorMessage !== null && <div className="muted">{entry.errorMessage}</div>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {audit.hasNextPage && (
        <button type="button" className="ghost" disabled={audit.isFetchingNextPage} onClick={() => void audit.fetchNextPage()}>
          {t('audit.loadMore')}
        </button>
      )}
    </>
  );
}
