/**
 * Where to go after signing in. Only paths inside this app are accepted: anything else (another host, a protocol-relative
 * URL, a backslash trick) would make the login page an open redirect.
 */
export function safeReturnUrl(value: string | null): string | null {
  if (value === null || !value.startsWith('/') || value.startsWith('//') || value.includes('\\')) {
    return null;
  }
  return value;
}

/** The login page URL that returns to <paramref name="target"/> (a path with query) after signing in. */
export function loginUrlFor(target: string): string {
  return `/login?returnUrl=${encodeURIComponent(target)}`;
}
