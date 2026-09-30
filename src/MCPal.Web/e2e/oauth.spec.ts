import { createHash, randomBytes } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { createClaudeKey, signUp } from './helpers';

test('Claude signs in with an API key and is sent back with a code', async ({ page, browser, request }) => {
  await signUp(page);
  const key = await createClaudeKey(page);

  // Claude registers itself (dynamic client registration) before it opens the sign-in page.
  const redirect = 'http://localhost:9999/callback';
  const registration = await request.post('/oauth/register', {
    data: { client_name: 'Playwright Claude', redirect_uris: [redirect], token_endpoint_auth_method: 'none' },
  });
  expect(registration.status()).toBe(201);
  const { client_id: clientId } = (await registration.json()) as { client_id: string };
  const verifier = randomBytes(32).toString('base64url');
  const challenge = createHash('sha256').update(verifier).digest('base64url');

  // A fresh browser context: Claude's user is not signed in to the portal.
  const claude = await browser.newContext();
  const authorize = await claude.newPage();
  await authorize.route('http://localhost:9999/**', (route) => route.fulfill({ status: 200, body: 'callback reached' }));
  const query = new URLSearchParams({
    client_id: clientId,
    redirect_uri: redirect,
    response_type: 'code',
    code_challenge: challenge,
    code_challenge_method: 'S256',
    state: 'st4te',
  });
  await authorize.goto(`/oauth/authorize?${query.toString()}`);
  await expect(authorize.getByRole('heading', { name: 'Connect Playwright Claude to MCPal' })).toBeVisible();

  await authorize.getByLabel('API key').fill(key);
  await authorize.getByRole('button', { name: 'Connect' }).click();

  await authorize.waitForURL(/localhost:9999\/callback\?/);
  const callback = new URL(authorize.url());
  expect(callback.searchParams.get('state')).toBe('st4te');
  expect(callback.searchParams.get('code')).toMatch(/.{20,}/);
  await claude.close();
});

test('a wrong API key is refused on the sign-in page', async ({ browser, request }) => {
  const redirect = 'http://localhost:9999/callback';
  const registration = await request.post('/oauth/register', {
    data: { client_name: 'Playwright Claude', redirect_uris: [redirect], token_endpoint_auth_method: 'none' },
  });
  const { client_id: clientId } = (await registration.json()) as { client_id: string };
  const challenge = createHash('sha256').update(randomBytes(32)).digest('base64url');
  const page = await (await browser.newContext()).newPage();

  const query = new URLSearchParams({ client_id: clientId, redirect_uri: redirect, response_type: 'code', code_challenge: challenge, code_challenge_method: 'S256' });
  await page.goto(`/oauth/authorize?${query.toString()}`);
  await page.getByLabel('API key').fill('mcpal_00000000_' + 'x'.repeat(40));
  await page.getByRole('button', { name: 'Connect' }).click();

  await expect(page.getByRole('alert')).toContainText('That API key is invalid, expired or revoked.');
});
