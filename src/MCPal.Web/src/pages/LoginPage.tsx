import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
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

  function submit(event: FormEvent) {
    event.preventDefault();
    login.mutate();
  }

  return (
    <main className="card narrow">
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
        <button type="submit" disabled={login.isPending}>
          {t('auth.login.submit')}
        </button>
      </form>
      <p className="muted">
        {t('auth.login.noAccount')} <Link to="/signup">{t('auth.signup.submit')}</Link>
      </p>
    </main>
  );
}
