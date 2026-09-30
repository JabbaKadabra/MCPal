import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { meQueryKey } from '../components/useMe';
import { t, type MessageKey } from '../i18n';

/** The page behind the link in the invitation mail: `/accept-invitation?token=…`. */
export function AcceptInvitationPage() {
  const [params] = useSearchParams();
  const token = params.get('token');
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [mismatch, setMismatch] = useState(false);
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const preview = useQuery({
    queryKey: ['invitation', token],
    queryFn: () => api.invitationPreview(token ?? ''),
    enabled: token !== null,
    retry: false,
  });
  const accept = useMutation({
    mutationFn: () => api.acceptInvitation(token ?? '', password),
    onSuccess: async (me) => {
      queryClient.setQueryData(meQueryKey, me);
      await navigate('/keys');
    },
  });

  if (token === null) {
    return (
      <main className="card narrow">
        <p role="alert">{t('invitation.invalidLink')}</p>
      </main>
    );
  }
  if (preview.isError) {
    return (
      <main className="card narrow">
        <ErrorText error={preview.error} />
      </main>
    );
  }
  if (preview.data === undefined) {
    return <p className="center muted">{t('common.loading')}</p>;
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (password !== repeat) {
      setMismatch(true);
      return;
    }
    setMismatch(false);
    accept.mutate();
  }

  const { companyName, email, role } = preview.data;
  return (
    <main className="card narrow">
      <h1>{t('invitation.title', { company: companyName })}</h1>
      <p className="muted">{t('invitation.intro', { role: t(`users.role.${role}` satisfies MessageKey), email })}</p>
      <p>
        <strong>{email}</strong>
      </p>
      <form onSubmit={submit}>
        <label>
          {t('invitation.password')}
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={10} autoComplete="new-password" />
        </label>
        <label>
          {t('invitation.repeat')}
          <input type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} required autoComplete="new-password" />
        </label>
        {mismatch && <p role="alert" className="errors">{t('invitation.mismatch')}</p>}
        <ErrorText error={accept.error} />
        <button type="submit" disabled={accept.isPending}>
          {t('invitation.submit')}
        </button>
      </form>
    </main>
  );
}
