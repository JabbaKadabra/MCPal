import { useMutation } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

export function ForgotPasswordPage() {
  const [email, setEmail] = useState('');
  const forgot = useMutation({ mutationFn: () => api.forgotPassword(email) });

  function submit(event: FormEvent) {
    event.preventDefault();
    forgot.mutate();
  }

  return (
    <main className="card narrow">
      <h1>{t('auth.forgot.title')}</h1>
      {forgot.isSuccess ? (
        <p role="status">{t('auth.forgot.done')}</p>
      ) : (
        <form onSubmit={submit}>
          <p className="muted">{t('auth.forgot.intro')}</p>
          <label>
            {t('auth.forgot.email')}
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="username" />
          </label>
          <ErrorText error={forgot.error} />
          <button type="submit" disabled={forgot.isPending}>
            {t('auth.forgot.submit')}
          </button>
        </form>
      )}
      <p className="muted">
        <Link to="/login">{t('auth.forgot.back')}</Link>
      </p>
    </main>
  );
}
