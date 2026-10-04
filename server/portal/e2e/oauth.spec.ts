import { createHash, randomBytes } from 'node:crypto';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { confirmEmail, password, signUp } from './helpers';

const redirect = 'http://localhost:9999/callback';

async function registerClient(request: APIRequestContext): Promise<string> {
  // Claude registers itself (dynamic client registration) before it opens the sign-in page.
  const registration = await request.post('/oauth/register', {
    data: { client_name: 'Playwright Claude', redirect_uris: [redirect], token_endpoint_auth_method: 'none' },
  });
  expect(registration.status()).toBe(201);
  const { client_id: clientId } = (await registration.json()) as { client_id: string };
  return clientId;
}

function authorizeQuery(clientId: string): string {
  const verifier = randomBytes(32).toString('base64url');
  return new URLSearchParams({
    client_id: clientId,
    redirect_uri: redirect,
    response_type: 'code',
    code_challenge: createHash('sha256').update(verifier).digest('base64url'),
    code_challenge_method: 'S256',
    state: 'st4te',
  }).toString();
}

test('Claude signs in with the portal login and is sent back with a code', async ({ page, browser, request }) => {
  const { email } = await signUp(page);
  await confirmEmail(page, email);
  const clientId = await registerClient(request);

  // A fresh browser context: Claude's user is not signed in to the portal yet.
  const claude = await browser.newContext();
  const authorize = await claude.newPage();
  await authorize.route('http://localhost:9999/**', (route) => route.fulfill({ status: 200, body: 'callback reached' }));
  await authorize.goto(`/oauth/authorize?${authorizeQuery(clientId)}`);

  // Without a session the authorize page sends the user to the login, which returns here afterwards.
  await expect(authorize.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await expect(authorize).toHaveURL(/\/login\?returnUrl=/);
  await authorize.getByLabel('Email').fill(email);
  await authorize.getByLabel('Password').fill(password);
  await authorize.getByRole('button', { name: 'Sign in' }).click();

  await expect(authorize.getByRole('heading', { name: 'Connect Playwright Claude to MCPal' })).toBeVisible();
  await authorize.getByRole('button', { name: new RegExp(`Connect as ${email}`) }).click();

  await authorize.waitForURL(/localhost:9999\/callback\?/);
  const callback = new URL(authorize.url());
  expect(callback.searchParams.get('state')).toBe('st4te');
  expect(callback.searchParams.get('code')).toMatch(/.{20,}/);
  await claude.close();
});

test('a user who is not signed in cannot pass the authorize page with a pasted key', async ({ browser, request }) => {
  const clientId = await registerClient(request);
  const page = await (await browser.newContext()).newPage();

  await page.goto(`/oauth/authorize?${authorizeQuery(clientId)}`);

  await expect(page).toHaveURL(/\/login\?returnUrl=/);
  await expect(page.getByLabel('API key')).toHaveCount(0);
  await expect(page.getByText('No account yet? Ask your admin for an invitation.')).toBeVisible();
});
