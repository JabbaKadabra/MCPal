import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import type { Role } from '../api/types';
import { ErrorText } from '../components/ErrorText';
import { Tag } from '../components/Tag';
import { t, type MessageKey } from '../i18n';

const teamQueryKey = ['team'] as const;
const roles: Role[] = ['member', 'owner'];

export function UsersPage() {
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<Role>('member');
  const [invitedEmail, setInvitedEmail] = useState<string | null>(null);
  const [viewing, setViewing] = useState<string | null>(null);
  const queryClient = useQueryClient();
  const team = useQuery({ queryKey: teamQueryKey, queryFn: api.team });
  const viewed = useQuery({ queryKey: ['user-access', viewing], queryFn: () => api.userAccess(viewing ?? ''), enabled: viewing !== null });
  const invite = useMutation({
    mutationFn: () => api.invite(email, role),
    onSuccess: async (invitation) => {
      setInvitedEmail(invitation.email);
      setEmail('');
      await queryClient.invalidateQueries({ queryKey: teamQueryKey });
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.removeUser(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamQueryKey }),
  });
  const setDisabled = useMutation({
    mutationFn: ({ id, disabled }: { id: string; disabled: boolean }) => (disabled ? api.disableUser(id) : api.enableUser(id)),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamQueryKey }),
  });
  const cancel = useMutation({
    mutationFn: (id: string) => api.cancelInvitation(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamQueryKey }),
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    setInvitedEmail(null);
    invite.mutate();
  }

  return (
    <>
      <h1>{t('users.title')}</h1>
      <p className="muted">{t('users.intro')}</p>

      <section className="panel">
        <h2>{t('users.invite.title')}</h2>
        <form className="row" onSubmit={submit}>
          <label className="grow">
            {t('users.invite.email')}
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="off" />
          </label>
          <label>
            {t('users.invite.role')}
            <select value={role} onChange={(e) => setRole(e.target.value as Role)}>
              {roles.map((option) => (
                <option key={option} value={option}>
                  {t(`users.role.${option}` satisfies MessageKey)}
                </option>
              ))}
            </select>
          </label>
          <button type="submit" disabled={invite.isPending}>
            {t('users.invite.submit')}
          </button>
        </form>
        {invitedEmail !== null && <p role="status" className="sent">{t('users.invite.sent', { email: invitedEmail })}</p>}
      </section>
      <ErrorText error={invite.error ?? remove.error ?? setDisabled.error ?? cancel.error ?? team.error} />

      {team.data !== undefined && (
        <>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{t('users.col.email')}</th>
                  <th>{t('users.col.role')}</th>
                  <th>{t('users.col.groups')}</th>
                  <th>{t('users.col.status')}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {team.data.users.map((member) => (
                  <tr key={member.id} className={member.disabled ? 'is-off' : undefined}>
                    <td>
                      {member.displayName != null && member.displayName !== '' ? `${member.displayName} · ` : ''}
                      {member.email}
                    </td>
                    <td>
                        <Tag tone={member.role === 'owner' ? 'ink' : 'plain'}>{t(`users.role.${member.role}` satisfies MessageKey)}</Tag>
                      </td>
                    <td>{member.groups.join(', ')}</td>
                    <td>
                        {member.disabled ? (
                          <Tag tone="off">{t('users.status.disabled')}</Tag>
                        ) : member.emailConfirmed ? (
                          <Tag tone="ok">{t('users.status.confirmed')}</Tag>
                        ) : (
                          <Tag tone="warn">{t('users.status.unconfirmed')}</Tag>
                        )}
                      </td>
                    <td>
                      <button type="button" className="ghost small" onClick={() => setViewing(viewing === member.id ? null : member.id)}>
                        {t('users.viewAccess')}
                      </button>{' '}
                      <button
                        type="button"
                        className="ghost small"
                        onClick={() => {
                          if (member.disabled || window.confirm(t('users.disableConfirm'))) {
                            setDisabled.mutate({ id: member.id, disabled: !member.disabled });
                          }
                        }}
                      >
                        {member.disabled ? t('users.enable') : t('users.disable')}
                      </button>{' '}
                      <button
                        type="button"
                        className="danger small"
                        onClick={() => {
                          if (window.confirm(t('users.removeConfirm'))) {
                            remove.mutate(member.id);
                          }
                        }}
                      >
                        {t('users.remove')}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {viewing !== null && viewed.data !== undefined && (
            <section className="panel" aria-label={t('users.access.title', { email: viewed.data.email })}>
              <h2>{t('users.access.title', { email: viewed.data.email })}</h2>
              <p>{viewed.data.groups.length === 0 ? t('users.access.noGroups') : viewed.data.groups.join(', ')}</p>
              {viewed.data.allTools && <p className="muted">{t('access.ownerAll')}</p>}
              {viewed.data.tools.length === 0 ? (
                <p className="muted">{t('access.noTools')}</p>
              ) : (
                <ul className="tools">
                  {viewed.data.tools.map((tool) => (
                    <li key={tool.publicName}>
                      <code>{tool.publicName}</code>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          )}

          {team.data.invitations.length > 0 && (
            <>
              <h2>{t('users.pending.title')}</h2>
              <div className="table-wrap">
                <table>
                  <tbody>
                    {team.data.invitations.map((invitation) => (
                      <tr key={invitation.id}>
                        <td>{invitation.email}</td>
                        <td>
                          <Tag tone={invitation.role === 'owner' ? 'ink' : 'plain'}>{t(`users.role.${invitation.role}` satisfies MessageKey)}</Tag>
                        </td>
                        <td className="muted">
                          {t('users.pending.expires')} {new Date(invitation.expiresAt).toLocaleDateString()}
                          {invitation.invitedBy !== null && ` · ${t('users.pending.invitedBy')} ${invitation.invitedBy}`}
                        </td>
                        <td>
                          <button type="button" className="ghost small" onClick={() => cancel.mutate(invitation.id)}>
                            {t('users.pending.cancel')}
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}
