import { useQuery } from '@tanstack/react-query';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { Tag } from '../components/Tag';
import { t } from '../i18n';

export function ConnectionsPage() {
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections, refetchInterval: 5000 });

  return (
    <>
      <h1>{t('connections.title')}</h1>
      <p className="muted">{t('connections.intro')}</p>
      <ErrorText error={connections.error} />
      {connections.data?.length === 0 && <p className="empty">{t('connections.empty')}</p>}
      {connections.data?.map((connection, index) => (
        <section className="card agent" key={`${connection.agentName}-${index}`}>
          <header className="agent-head">
            <h2>{connection.agentName}</h2>
            <span className="led">{t('connections.online')}</span>
            <Tag tone="plain">{t('connections.version', { version: connection.agentVersion })}</Tag>
          </header>
          <p className="agent-meta">
            <span>
              {t('connections.since')} {new Date(connection.connectedAt).toLocaleString()}
            </span>
            {connection.apiKeyName !== null && <span>{`${t('connections.key')} ${connection.apiKeyName}`}</span>}
          </p>
          {connection.updateAvailable && connection.latestAgentVersion !== null && (
            <p className="notice">{t('connections.update', { latest: connection.latestAgentVersion })}</p>
          )}
          <ul className="servers">
            {connection.servers.map((server) => (
              <li key={server.name} className="server">
                <div className="server-head">
                  <strong>{server.name}</strong>
                  <Tag tone="agent">{`${server.tools.length} ${t('connections.tools')}`}</Tag>
                </div>
                <ul className="tools">
                  {server.tools.map((tool) => (
                    <li key={tool}>
                      <code>{tool}</code>
                    </li>
                  ))}
                </ul>
              </li>
            ))}
          </ul>
          {(connection.rejected.length > 0 || connection.rejectedTools.length > 0) && (
            <ul className="errors">
              {connection.rejected.map((rejected) => (
                <li key={rejected.server}>
                  <strong>{t('connections.rejected')}</strong> {`${rejected.server} — ${rejected.reason}`}
                </li>
              ))}
              {connection.rejectedTools.map((rejected) => (
                <li key={`${rejected.server}/${rejected.tool}`}>
                  <strong>{t('connections.rejectedTool')}</strong> {`${rejected.server} / ${rejected.tool} — ${rejected.reason}`}
                </li>
              ))}
            </ul>
          )}
        </section>
      ))}
    </>
  );
}
