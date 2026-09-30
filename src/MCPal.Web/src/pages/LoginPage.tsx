import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { AuthFrame } from '../components/AuthFrame';
import { ErrorText } from '../components/ErrorText';
import { meQueryKey } from '../components/useMe';
import { t } from '../i18n';

export function LoginPage() {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const login = useMutation({
    mutationFn: () => api.login(email, password),
    onSuccess: async (me) => {
      queryClient.setQueryData(meQueryKey, me);
      await navigate('/keys');
    },
  });

  const resend = useMutation({ mutationFn: () => api.resendConfirmation(email) });
  const notConfirmed = login.error instanceof ApiError && login.error.code === 'email_not_confirmed';

  function submit(event: FormEvent) {
    event.preventDefault();
    login.mutate();
  }

  return (
    <AuthFrame>
      <h1>{t('auth.login.title')}</h1>
      <form onSubmit={submit}>
        <label>
          {t('auth.login.email')}
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="username" />
        </label>
        <label>
          {t('auth.login.password')}
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            autoComplete="current-password"
          />
        </label>
        <ErrorText error={login.error} />
        {notConfirmed && (
          <>
            <button type="button" className="ghost" disabled={resend.isPending} onClick={() => resend.mutate()}>
              {t('auth.login.resend')}
            </button>
            {resend.isSuccess && <p role="status">{t('auth.login.resent')}</p>}
          </>
        )}
        <button type="submit" disabled={login.isPending}>
          {t('auth.login.submit')}
        </button>
      </form>
      <p className="muted">
        <Link to="/forgot-password">{t('auth.login.forgot')}</Link>
      </p>
      <p className="muted">
        {t('auth.login.noAccount')} <Link to="/signup">{t('auth.signup.submit')}</Link>
      </p>
    </AuthFrame>
  );
}
