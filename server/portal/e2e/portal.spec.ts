import { expect, test } from '@playwright/test';
import { createPersonalToken, password, signUp, unique } from './helpers';

test('sign up, create a key, see it only once, revoke it', async ({ page }) => {
  page.on('dialog', (dialog) => void dialog.accept());
  await signUp(page);
  const name = unique('ci-key');

  const key = await createPersonalToken(page, name);
  await expect(page.getByText('Copy your new key now')).toBeVisible();

  // The full key is gone after a reload; only the prefix is listed.
  await page.reload();
  await expect(page.getByTestId('created-key')).toHaveCount(0);
  const row = page.getByRole('row', { name: new RegExp(name) });
  await expect(row).toContainText(key.slice(0, 21));
  await expect(row).not.toContainText(key);
  await expect(row).toContainText('Active');

  await row.getByRole('button', { name: 'Revoke' }).click();
  await expect(row).toContainText('Revoked');
});

test('a wrong password shows the failure message', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email').fill('nobody@example.test');
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByRole('alert')).toContainText('Invalid email or password.');
  await expect(page).toHaveURL(/\/login$/);
});

test('the password reset page answers the same for any address', async ({ page }) => {
  await page.goto('/login');
  await page.getByRole('link', { name: 'Forgot your password?' }).click();

  // The router commits the new page a moment after the URL changes; typing into the old page would lose the text.
  await expect(page.getByRole('heading', { name: 'Reset your password' })).toBeVisible();
  await page.getByLabel('Email').fill('nobody@example.test');
  await page.getByRole('button', { name: 'Send reset link' }).click();

  await expect(page.getByText('If an account exists for this address, we sent a link')).toBeVisible();
});

test('a new owner lands on the setup walkthrough and gets a pre-filled mcpal.json and a bridge key', async ({ page, baseURL }) => {
  const email = `${unique('owner')}@example.test`;
  await page.goto('/signup');
  await page.getByLabel('Company name').fill(unique('Acme'));
  await page.getByLabel('Work email').fill(email);
  await page.getByLabel('Password (at least 10 characters)').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();

  await expect(page).toHaveURL(/\/setup$/);
  await expect(page.getByRole('heading', { name: 'Set up your bridge' })).toBeVisible();

  await page.getByRole('button', { name: 'Create bridge key' }).click();
  const key = await page.getByTestId('created-key').innerText();
  expect(key).toMatch(/^mcpal_[0-9a-f]{8}_[A-Za-z0-9]{40}$/);
  await expect(page.getByText(`-e MCPAL_API_KEY=${key} `)).toBeVisible();
  await page.getByRole('radio', { name: 'Linux x64' }).check();
  await expect(page.getByText(`sudo ./install.sh --api-key ${key}`)).toBeVisible();

  const href = (await page.getByRole('link', { name: 'Download mcpal.json' }).getAttribute('href')) ?? '';
  const config = JSON.parse(decodeURIComponent(href.split(',')[1] ?? '')) as { mcpal: { url: string; apiKey?: string } };
  expect(config.mcpal.url).toBe((baseURL ?? '').replace(/\/$/, ''));
  expect(config.mcpal.apiKey).toBeUndefined();
  await expect(page.getByText('sudo install -m 640 ../mcp.json /etc/mcpal/mcp.json')).toBeVisible();
});
