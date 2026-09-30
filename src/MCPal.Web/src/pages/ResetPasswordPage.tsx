import { useMutation } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

/** The page behind the link in the reset mail: `/reset-password?email=…&token=…`. */
export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const email = params.get('email');
  const token = params.get('token');
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [mismatch, setMismatch] = useState(false);
  const reset = useMutation({ mutationFn: () => api.resetPassword(email ?? '', token ?? '', password) });

  if (email === null || token === null) {
    return (
      <main className="card narrow">
        <p role="alert">{t('auth.reset.invalidLink')}</p>
        <p>
          <Link to="/forgot-password">{t('auth.reset.askAgain')}</Link>
        </p>
      </main>
    );
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (password !== repeat) {
      setMismatch(true);
      return;
    }
    setMismatch(false);
    reset.mutate();
  }

  return (
    <main className="card narrow">
      <h1>{t('auth.reset.title')}</h1>
      {reset.isSuccess ? (
        <>
          <p role="status">{t('auth.reset.done')}</p>
          <Link to="/login">{t('auth.signIn')}</Link>
        </>
      ) : (
        <form onSubmit={submit}>
          <label>
            {t('auth.reset.password')}
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={10} autoComplete="new-password" />
          </label>
          <label>
            {t('auth.reset.repeat')}
            <input type="password" value={repeat} onChange={(e) => setRepeat(e.target.value)} required autoComplete="new-password" />
          </label>
          {mismatch && <p role="alert" className="errors">{t('auth.reset.mismatch')}</p>}
          <ErrorText error={reset.error} />
          <button type="submit" disabled={reset.isPending}>
            {t('auth.reset.submit')}
          </button>
        </form>
      )}
    </main>
  );
}
