import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { AuthFrame } from '../components/AuthFrame';
import { ErrorText } from '../components/ErrorText';
import { meQueryKey } from '../components/useMe';
import { t } from '../i18n';

export function SignupPage() {
  const [company, setCompany] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const signup = useMutation({
    mutationFn: () => api.signup(company, email, password),
    onSuccess: async (me) => {
      queryClient.setQueryData(meQueryKey, me);
      await navigate('/keys');
    },
  });

  function submit(event: FormEvent) {
    event.preventDefault();
    signup.mutate();
  }

  return (
    <AuthFrame>
      <h1>{t('auth.signup.title')}</h1>
      <form onSubmit={submit}>
        <label>
          {t('auth.signup.company')}
          <input value={company} onChange={(e) => setCompany(e.target.value)} required autoComplete="organization" />
        </label>
        <label>
          {t('auth.signup.email')}
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="email" />
        </label>
        <label>
          {t('auth.signup.password')}
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
            minLength={10}
            autoComplete="new-password"
          />
        </label>
        <ErrorText error={signup.error} />
        <button type="submit" disabled={signup.isPending}>
          {t('auth.signup.submit')}
        </button>
      </form>
      <p className="muted">
        {t('auth.signup.haveAccount')} <Link to="/login">{t('auth.login.submit')}</Link>
      </p>
    </AuthFrame>
  );
}
