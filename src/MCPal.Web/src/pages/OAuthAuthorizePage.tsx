import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { useLocation } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { ErrorText } from '../components/ErrorText';
import { buildAuthorizeBody, buildDenyUrl, parseAuthorizeParams } from '../oauth/authorizeParams';
import { t } from '../i18n';

/** The sign-in page Claude opens. The user proves access to a company by pasting an API key or by using the portal session. */
export function OAuthAuthorizePage() {
  const { search } = useLocation();
  const params = parseAuthorizeParams(search);
  const [apiKey, setApiKey] = useState('');
  const context = useQuery({
    queryKey: ['authorize-context', search],
    queryFn: () => api.authorizeContext(search),
    enabled: params !== null,
  });
  const authorize = useMutation({
    mutationFn: (credential: { apiKey: string } | { useSession: true }) => {
      if (params === null) {
        throw new Error('Missing authorization parameters');
      }
      return api.authorize(buildAuthorizeBody(params, credential));
    },
    onSuccess: ({ redirectUrl }) => {
      window.location.assign(redirectUrl);
    },
  });

  if (params === null || context.isError) {
    return (
      <main className="card narrow">
        <p role="alert">{context.error instanceof ApiError ? context.error.message : t('oauth.invalidRequest')}</p>
      </main>
    );
  }
  if (context.data === undefined) {
    return <p className="center muted">{t('common.loading')}</p>;
  }

  const { clientName, redirectHost, signedInCompany } = context.data;

  function submit(event: FormEvent) {
    event.preventDefault();
    authorize.mutate({ apiKey });
  }

  const keyRejected = authorize.error instanceof ApiError && authorize.error.code === 'invalid_key';

  return (
    <main className="card narrow">
      <h1>{t('oauth.title', { client: clientName })}</h1>
      <p className="muted">{t('oauth.redirectNote', { host: redirectHost })}</p>

      {signedInCompany !== null && (
        <>
          <button type="button" onClick={() => authorize.mutate({ useSession: true })} disabled={authorize.isPending}>
            {t('oauth.signedIn', { company: signedInCompany })}
          </button>
          <p className="muted center">{t('oauth.or')}</p>
        </>
      )}

      <form onSubmit={submit}>
        <label>
          {t('oauth.keyLabel')}
          <input
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder={t('oauth.keyPlaceholder')}
            autoComplete="off"
            spellCheck={false}
            required
          />
        </label>
        {keyRejected ? <p role="alert" className="errors">{t('oauth.invalidKey')}</p> : <ErrorText error={authorize.error} />}
        <div className="row">
          <button type="submit" disabled={authorize.isPending}>
            {t('oauth.submit')}
          </button>
          <button type="button" className="ghost" onClick={() => window.location.assign(buildDenyUrl(params))}>
            {t('oauth.deny')}
          </button>
        </div>
      </form>
    </main>
  );
}
