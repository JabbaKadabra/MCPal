import { expect, type Page } from '@playwright/test';

export const password = 'correct-horse-battery';

/** A name that is unique per call, so tests never collide in the shared database. */
export function unique(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/** Creates a company and its owner through the sign-up page and lands on the API keys page. */
export async function signUp(page: Page, company = unique('Acme')): Promise<{ company: string; email: string }> {
  const email = `${unique('owner')}@example.test`;
  await page.goto('/signup');
  await page.getByLabel('Company name').fill(company);
  await page.getByLabel('Work email').fill(email);
  await page.getByLabel('Password (at least 10 characters)').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByRole('heading', { name: 'API keys' })).toBeVisible();
  return { company, email };
}

/** Creates a key for Claude on the API keys page and returns the full key shown once. */
export async function createClaudeKey(page: Page, name = unique('key')): Promise<string> {
  await page.getByLabel('Key name').fill(name);
  await page.getByRole('button', { name: 'Create key' }).click();
  const key = await page.getByTestId('created-key').innerText();
  expect(key).toMatch(/^mcpal_[0-9a-f]{8}_[A-Za-z0-9]{40}$/);
  return key;
}
