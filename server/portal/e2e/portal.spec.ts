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
