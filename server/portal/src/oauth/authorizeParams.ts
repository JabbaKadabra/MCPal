/** The OAuth authorization request parameters Claude puts on the sign-in page URL. */
export interface AuthorizeParams {
  client_id: string;
  redirect_uri: string;
  response_type: string;
  code_challenge: string;
  code_challenge_method: string;
  state: string;
  scope: string;
  resource: string;
}

/** Returns null when the link lacks the parameters needed to identify the client and where to send the user back. */
export function parseAuthorizeParams(search: string): AuthorizeParams | null {
  const query = new URLSearchParams(search);
  const clientId = query.get('client_id');
  const redirectUri = query.get('redirect_uri');
  if (!clientId || !redirectUri) {
    return null;
  }
  return {
    client_id: clientId,
    redirect_uri: redirectUri,
    response_type: query.get('response_type') ?? '',
    code_challenge: query.get('code_challenge') ?? '',
    code_challenge_method: query.get('code_challenge_method') ?? '',
    state: query.get('state') ?? '',
    scope: query.get('scope') ?? '',
    resource: query.get('resource') ?? '',
  };
}

/** The body of the authorization call. The user is identified by the portal session, so there is no credential in it. */
export function buildAuthorizeBody(params: AuthorizeParams): Record<string, unknown> {
  return {
    ...params,
    resource: params.resource || undefined,
    scope: params.scope || undefined,
    state: params.state || undefined,
  };
}

/** Where to send the user when they cancel: the registered redirect URI with error=access_denied. */
export function buildDenyUrl(params: AuthorizeParams): string {
  const url = new URL(params.redirect_uri);
  url.searchParams.set('error', 'access_denied');
  if (params.state) {
    url.searchParams.set('state', params.state);
  }
  return url.toString();
}
