import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { Grant, Group, TeamMember } from '../api/types';
import { isValidToolPattern, parsePatterns, previewGrant, type OnlineTool } from '../access/glob';
import { ErrorText } from '../components/ErrorText';
import { Tag } from '../components/Tag';
import { t } from '../i18n';

const groupsQueryKey = ['groups'] as const;
const teamQueryKey = ['team'] as const;

/** Online tools as (server, tool) pairs, for the preview of a grant. */
function onlineTools(connections: { servers: { name: string; tools: string[] }[] }[] | undefined): OnlineTool[] {
  return (connections ?? []).flatMap((connection) => connection.servers.flatMap((server) => server.tools.map((tool) => ({ server: server.name, tool }))));
}

interface GrantFormProps {
  initial?: Grant;
  servers: string[];
  online: OnlineTool[];
  pending: boolean;
  error: unknown;
  onSubmit: (serverPattern: string, toolPatterns: string[]) => void;
  onCancel?: () => void;
}

/** Adds or edits a grant: a server glob, tool globs and a live preview of the online tools they allow. */
function GrantForm({ initial, servers, online, pending, error, onSubmit, onCancel }: GrantFormProps) {
  const [serverPattern, setServerPattern] = useState(initial?.serverPattern ?? '*');
  const [toolsText, setToolsText] = useState(initial?.toolPatterns.join(', ') ?? '*');
  const toolPatterns = parsePatterns(toolsText);
  const invalid = toolPatterns.filter((pattern) => !isValidToolPattern(pattern));
  const matches = previewGrant(serverPattern.trim(), toolPatterns.filter(isValidToolPattern), online);
  const listId = `servers-${initial?.id ?? 'new'}`;

  function submit(event: FormEvent) {
    event.preventDefault();
    onSubmit(serverPattern.trim(), toolPatterns);
  }

  return (
    <form className="grant-form" onSubmit={submit}>
      <div className="row">
        <label className="grow">
          {t('groups.grant.server')}
          <input value={serverPattern} onChange={(e) => setServerPattern(e.target.value)} list={listId} autoComplete="off" required />
          <datalist id={listId}>
            {servers.map((server) => (
              <option key={server} value={server} />
            ))}
          </datalist>
        </label>
        <label className="grow">
          {t('groups.grant.tools')}
          <input value={toolsText} onChange={(e) => setToolsText(e.target.value)} autoComplete="off" spellCheck={false} required />
        </label>
        <button type="submit" disabled={pending || toolPatterns.length === 0 || invalid.length > 0}>
          {initial === undefined ? t('groups.grant.add') : t('groups.grant.save')}
        </button>
        {onCancel !== undefined && (
          <button type="button" className="ghost" onClick={onCancel}>
            {t('groups.cancel')}
          </button>
        )}
      </div>
      <p className="muted">{t('groups.grant.help')}</p>
      {invalid.length > 0 && <p role="alert" className="errors">{t('groups.grant.invalid', { patterns: invalid.join(', ') })}</p>}
      <p className="muted" data-testid="grant-preview">
        {matches.length === 0
          ? t('groups.grant.previewNone')
          : t('groups.grant.preview', { count: String(matches.length), tools: matches.map((m) => `${m.server}/${m.tool}`).join(', ') })}
      </p>
      <ErrorText error={error} />
    </form>
  );
}

interface GroupCardProps {
  group: Group;
  users: TeamMember[];
  servers: string[];
  online: OnlineTool[];
}

function GroupCard({ group, users, servers, online }: GroupCardProps) {
  const queryClient = useQueryClient();
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(group.name);
  const [editingGrant, setEditingGrant] = useState<string | null>(null);
  const refresh = () => queryClient.invalidateQueries({ queryKey: groupsQueryKey });
  const rename = useMutation({
    mutationFn: () => api.renameGroup(group.id, name),
    onSuccess: async () => {
      setRenaming(false);
      await refresh();
    },
  });
  const remove = useMutation({ mutationFn: () => api.deleteGroup(group.id), onSuccess: refresh });
  const setMembers = useMutation({ mutationFn: (userIds: string[]) => api.setGroupMembers(group.id, userIds), onSuccess: refresh });
  const addGrant = useMutation({ mutationFn: (args: { server: string; tools: string[] }) => api.addGrant(group.id, args.server, args.tools), onSuccess: refresh });
  const updateGrant = useMutation({
    mutationFn: (args: { id: string; server: string; tools: string[] }) => api.updateGrant(args.id, args.server, args.tools),
    onSuccess: async () => {
      setEditingGrant(null);
      await refresh();
    },
  });
  const removeGrant = useMutation({ mutationFn: (id: string) => api.deleteGrant(id), onSuccess: refresh });

  function toggleMember(userId: string, checked: boolean) {
    setMembers.mutate(checked ? [...group.memberIds, userId] : group.memberIds.filter((id) => id !== userId));
  }

  return (
    <section className="panel" aria-label={group.name}>
      <header className="row">
        {renaming ? (
          <form
            className="row grow"
            onSubmit={(event) => {
              event.preventDefault();
              rename.mutate();
            }}
          >
            <input aria-label={t('groups.name')} value={name} onChange={(e) => setName(e.target.value)} required />
            <button type="submit" className="small" disabled={rename.isPending}>
              {t('groups.save')}
            </button>
            <button type="button" className="ghost small" onClick={() => setRenaming(false)}>
              {t('groups.cancel')}
            </button>
          </form>
        ) : (
          <>
            <h2 className="grow">{group.name}</h2>
            {group.isEveryone ? (
              <Tag tone="ink">{t('groups.everyone.tag')}</Tag>
            ) : (
              <>
                <button type="button" className="ghost small" onClick={() => setRenaming(true)}>
                  {t('groups.rename')}
                </button>
                <button
                  type="button"
                  className="danger small"
                  onClick={() => {
                    if (window.confirm(t('groups.deleteConfirm', { name: group.name }))) {
                      remove.mutate();
                    }
                  }}
                >
                  {t('groups.delete')}
                </button>
              </>
            )}
          </>
        )}
      </header>
      <ErrorText error={rename.error ?? remove.error ?? setMembers.error ?? removeGrant.error} />

      <h3>{t('groups.members')}</h3>
      {group.isEveryone ? (
        <p className="muted">{t('groups.everyone.help')}</p>
      ) : users.length === 0 ? (
        <p className="muted">{t('groups.noUsers')}</p>
      ) : (
        <ul className="checklist">
          {users.map((user) => (
            <li key={user.id}>
              <label>
                <input
                  type="checkbox"
                  checked={group.memberIds.includes(user.id)}
                  disabled={setMembers.isPending}
                  onChange={(e) => toggleMember(user.id, e.target.checked)}
                />
                {user.email}
              </label>
            </li>
          ))}
        </ul>
      )}

      <h3>{t('groups.grants')}</h3>
      {group.grants.length === 0 && <p className="muted">{t('groups.grants.none')}</p>}
      {group.grants.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>{t('groups.grant.server')}</th>
                <th>{t('groups.grant.tools')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {group.grants.map((grant) =>
                editingGrant === grant.id ? (
                  <tr key={grant.id}>
                    <td colSpan={3}>
                      <GrantForm
                        initial={grant}
                        servers={servers}
                        online={online}
                        pending={updateGrant.isPending}
                        error={updateGrant.error}
                        onSubmit={(server, tools) => updateGrant.mutate({ id: grant.id, server, tools })}
                        onCancel={() => setEditingGrant(null)}
                      />
                    </td>
                  </tr>
                ) : (
                  <tr key={grant.id}>
                    <td>
                      <code>{grant.serverPattern}</code>
                    </td>
                    <td>
                      <code>{grant.toolPatterns.join(', ')}</code>
                    </td>
                    <td>
                      <button type="button" className="ghost small" onClick={() => setEditingGrant(grant.id)}>
                        {t('groups.grant.edit')}
                      </button>{' '}
                      <button type="button" className="danger small" onClick={() => removeGrant.mutate(grant.id)}>
                        {t('groups.grant.remove')}
                      </button>
                    </td>
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
      )}
      <GrantForm
        servers={servers}
        online={online}
        pending={addGrant.isPending}
        error={addGrant.error}
        onSubmit={(server, tools) => addGrant.mutate({ server, tools })}
      />
    </section>
  );
}

/** Owners manage groups here: who is in a group and which servers and tools its members may use. */
export function GroupsPage() {
  const [name, setName] = useState('');
  const queryClient = useQueryClient();
  const groups = useQuery({ queryKey: groupsQueryKey, queryFn: api.groups });
  const team = useQuery({ queryKey: teamQueryKey, queryFn: api.team });
  const connections = useQuery({ queryKey: ['connections'], queryFn: api.connections });
  const create = useMutation({
    mutationFn: () => api.createGroup(name),
    onSuccess: async () => {
      setName('');
      await queryClient.invalidateQueries({ queryKey: groupsQueryKey });
    },
  });

  const online = onlineTools(connections.data);
  const servers = [...new Set(online.map((tool) => tool.server))];

  return (
    <>
      <h1>{t('groups.title')}</h1>
      <p className="muted">{t('groups.intro')}</p>
      <section className="panel">
        <form
          className="row"
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate();
          }}
        >
          <label className="grow">
            {t('groups.name')}
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder={t('groups.namePlaceholder')} required />
          </label>
          <button type="submit" disabled={create.isPending}>
            {t('groups.create')}
          </button>
        </form>
        <ErrorText error={create.error} />
      </section>
      <ErrorText error={groups.error ?? team.error} />
      {groups.data?.map((group) => (
        <GroupCard key={group.id} group={group} users={team.data?.users ?? []} servers={servers} online={online} />
      ))}
    </>
  );
}
