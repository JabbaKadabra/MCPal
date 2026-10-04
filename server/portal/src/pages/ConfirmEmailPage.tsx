import { useMutation } from '@tanstack/react-query';
import { useEffect, useRef } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { AuthFrame } from '../components/AuthFrame';
import { ErrorText } from '../components/ErrorText';
import { t } from '../i18n';

/** The page behind the link in the confirmation mail: `/confirm-email?userId=…&token=…`. The link is used once, on load. */
export function ConfirmEmailPage() {
  const [params] = useSearchParams();
  const userId = params.get('userId');
  const token = params.get('token');
  const started = useRef(false);
  const confirm = useMutation({ mutationFn: () => api.confirmEmail(userId ?? '', token ?? '') });
  const { mutate } = confirm;

  useEffect(() => {
    if (userId !== null && token !== null && !started.current) {
      started.current = true;
      mutate();
    }
  }, [userId, token, mutate]);

  return (
    <AuthFrame>
      <h1>{t('auth.confirm.title')}</h1>
      {(userId === null || token === null) && <p role="alert">{t('auth.confirm.invalidLink')}</p>}
      {confirm.isPending && <p className="muted">{t('auth.confirm.working')}</p>}
      {confirm.isSuccess && <p role="status">{t('auth.confirm.done')}</p>}
      <ErrorText error={confirm.error} />
      <p>
        <Link to="/login">{t('auth.signIn')}</Link>
      </p>
    </AuthFrame>
  );
}
