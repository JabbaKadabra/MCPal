import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { loginUrlFor } from '../auth/returnUrl';
import { AuthFrame } from '../components/AuthFrame';
import { ErrorText } from '../components/ErrorText';
import { meQueryKey } from '../components/useMe';
import { buildAuthorizeBody, buildDenyUrl, parseAuthorizeParams } from '../oauth/authorizeParams';
import { t } from '../i18n';

/**
 * The page Claude opens. Signing in to the portal is the only way to authorize: the token Claude receives acts as the
 * signed-in user. Without a session the page sends the user to the login and comes back here afterwards.
 */
export function OAuthAuthorizePage() {
  const { pathname, search } = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const params = parseAuthorizeParams(search);
  const here = `${pathname}${search}`;
  const context = useQuery({
    queryKey: ['authorize-context', search],
    queryFn: () => api.authorizeContext(search),
    enabled: params !== null,
  });
  const authorize = useMutation({
    mutationFn: () => {
      if (params === null) {
        throw new Error('Missing authorization parameters');
      }
      return api.authorize(buildAuthorizeBody(params));
    },
    onSuccess: ({ redirectUrl }) => {
      window.location.assign(redirectUrl);
    },
  });
  const signOut = useMutation({
    mutationFn: api.logout,
    onSuccess: async () => {
      queryClient.clear();
      queryClient.setQueryData(meQueryKey, null);
      await navigate(loginUrlFor(here));
    },
  });

  const signedIn = context.data?.signedInEmail != null;
  const loginRequired = context.data !== undefined && !signedIn;
  useEffect(() => {
    if (loginRequired) {
      void navigate(loginUrlFor(here), { replace: true });
    }
  }, [loginRequired, navigate, here]);

  if (params === null || context.isError) {
    return (
      <AuthFrame>
        <p role="alert">{context.error instanceof ApiError ? context.error.message : t('oauth.invalidRequest')}</p>
      </AuthFrame>
    );
  }
  if (context.data === undefined || !signedIn) {
    return (
      <AuthFrame>
        <p className="muted">{t('common.loading')}</p>
      </AuthFrame>
    );
  }

  const { clientName, redirectHost, signedInEmail, signedInCompany } = context.data;
  // The session ended between loading this page and clicking: sign in again and come back.
  const sessionGone = authorize.error instanceof ApiError && authorize.error.code === 'login_required';

  return (
    <AuthFrame>
      <h1>{t('oauth.title', { client: clientName })}</h1>
      <p className="muted">{t('oauth.redirectNote', { host: redirectHost })}</p>

      {sessionGone ? (
        <button type="button" className="wide" onClick={() => void navigate(loginUrlFor(here))}>
          {t('auth.signIn')}
        </button>
      ) : (
        <>
          <button type="button" className="wide" onClick={() => authorize.mutate()} disabled={authorize.isPending}>
            {t('oauth.connectAs', { email: signedInEmail ?? '', company: signedInCompany ?? '' })}
          </button>
          <ErrorText error={authorize.error} />
        </>
      )}
      <div className="row">
        <button type="button" className="ghost" disabled={signOut.isPending} onClick={() => signOut.mutate()}>
          {t('oauth.notYou')}
        </button>
        <button type="button" className="ghost" onClick={() => window.location.assign(buildDenyUrl(params))}>
          {t('oauth.deny')}
        </button>
      </div>
    </AuthFrame>
  );
}
