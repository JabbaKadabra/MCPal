import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

export function ConnectionsPage() {
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections, refetchInterval: 5000 });

  return (
    <>
      <h1>{t('connections.title')}</h1>
      <p className="muted">{t('connections.intro')}</p>
      <ErrorText error={connections.error} />
      {connections.data?.length === 0 && <p className="muted">{t('connections.empty')}</p>}
      {connections.data?.map((connection, index) => (
        <section className="card" key={`${connection.agentName}-${index}`}>
          <h2>{connection.agentName}</h2>
          <p className="muted">
            {t('connections.version', { version: connection.agentVersion })} · {t('connections.since')} {new Date(connection.connectedAt).toLocaleString()}
            {connection.apiKeyName !== null && ` · ${t('connections.key')} ${connection.apiKeyName}`}
          </p>
          {connection.updateAvailable && connection.latestAgentVersion !== null && (
            <p className="notice">{t('connections.update', { latest: connection.latestAgentVersion })}</p>
          )}
          <ul>
            {connection.servers.map((server) => (
              <li key={server.name}>
                <strong>{server.name}</strong> — {server.tools.length} {t('connections.tools')}
                <div className="muted">{server.tools.join(', ')}</div>
              </li>
            ))}
          </ul>
          {(connection.rejected.length > 0 || connection.rejectedTools.length > 0) && (
            <ul className="errors">
              {connection.rejected.map((rejected) => (
                <li key={rejected.server}>
                  {t('connections.rejected')}: {rejected.server} — {rejected.reason}
                </li>
              ))}
              {connection.rejectedTools.map((rejected) => (
                <li key={`${rejected.server}/${rejected.tool}`}>
                  {t('connections.rejectedTool')}: {`${rejected.server} / ${rejected.tool}`} — {rejected.reason}
                </li>
              ))}
            </ul>
          )}
        </section>
      ))}
    </>
  );
}
