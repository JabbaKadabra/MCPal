import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { resetCsrfToken } from '../api/client';
import { mockFetch, type RecordedCall } from '../test/fetchMock';
import { ConfirmEmailPage } from './ConfirmEmailPage';
import { ForgotPasswordPage } from './ForgotPasswordPage';
import { LoginPage } from './LoginPage';
import { ResetPasswordPage } from './ResetPasswordPage';

function renderAt(path: string, page: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>{page}</MemoryRouter>
    </QueryClientProvider>,
  );
}

function csrfThen(handler: (call: RecordedCall) => { status?: number; body?: unknown } | undefined) {
  return (call: RecordedCall) => (call.url === '/api/portal/csrf' ? { body: { token: 't' } } : handler(call));
}

describe('account pages', () => {
  beforeEach(() => {
    resetCsrfToken();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  describe('ForgotPasswordPage', () => {
    it('asks for the reset mail and always shows the same neutral answer', async () => {
      const calls = mockFetch(csrfThen(() => ({ status: 204 })));
      const user = userEvent.setup();
      renderAt('/forgot-password', <ForgotPasswordPage />);

      await user.type(screen.getByLabelText('Email'), 'nobody@acme.example');
      await user.click(screen.getByRole('button', { name: 'Send reset link' }));

      expect(await screen.findByText(/If an account exists for this address, we sent a link/)).toBeInTheDocument();
      expect(calls.find((c) => c.url === '/api/portal/auth/forgot-password')?.body).toEqual({ email: 'nobody@acme.example' });
    });
  });

  describe('ResetPasswordPage', () => {
    const path = '/reset-password?email=a%40acme.example&token=abc_DEF-123';

    it('sends email, token and the new password from the link', async () => {
      const calls = mockFetch(csrfThen(() => ({ status: 204 })));
      const user = userEvent.setup();
      renderAt(path, <ResetPasswordPage />);

      await user.type(screen.getByLabelText('New password (at least 10 characters)'), 'a-new-long-password');
      await user.type(screen.getByLabelText('Repeat the new password'), 'a-new-long-password');
      await user.click(screen.getByRole('button', { name: 'Set new password' }));

      expect(await screen.findByText('Your password was changed.')).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login');
      expect(calls.find((c) => c.url === '/api/portal/auth/reset-password')?.body).toEqual({
        email: 'a@acme.example',
        token: 'abc_DEF-123',
        newPassword: 'a-new-long-password',
      });
    });

    it('does not send when the two passwords differ', async () => {
      const calls = mockFetch(csrfThen(() => ({ status: 204 })));
      const user = userEvent.setup();
      renderAt(path, <ResetPasswordPage />);

      await user.type(screen.getByLabelText('New password (at least 10 characters)'), 'a-new-long-password');
      await user.type(screen.getByLabelText('Repeat the new password'), 'something-else-entirely');
      await user.click(screen.getByRole('button', { name: 'Set new password' }));

      expect(await screen.findByText('The two passwords are not the same.')).toBeInTheDocument();
      expect(calls.some((c) => c.url === '/api/portal/auth/reset-password')).toBe(false);
    });

    it('shows the server reasons when the password is rejected', async () => {
      mockFetch(csrfThen(() => ({ status: 400, body: { errors: ['This link is invalid or has expired.'] } })));
      const user = userEvent.setup();
      renderAt(path, <ResetPasswordPage />);

      await user.type(screen.getByLabelText('New password (at least 10 characters)'), 'a-new-long-password');
      await user.type(screen.getByLabelText('Repeat the new password'), 'a-new-long-password');
      await user.click(screen.getByRole('button', { name: 'Set new password' }));

      expect(await screen.findByText('This link is invalid or has expired.')).toBeInTheDocument();
    });

    it('explains a link without token', () => {
      mockFetch(() => undefined);

      renderAt('/reset-password', <ResetPasswordPage />);

      expect(screen.getByText('This link is not valid. Ask for a new one.')).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Set new password' })).not.toBeInTheDocument();
    });
  });

  describe('ConfirmEmailPage', () => {
    it('confirms once on load and offers to sign in', async () => {
      const calls = mockFetch(csrfThen(() => ({ status: 204 })));

      renderAt('/confirm-email?userId=u1&token=tok', <ConfirmEmailPage />);

      expect(await screen.findByText('Your email address is confirmed.')).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login');
      expect(calls.filter((c) => c.url === '/api/portal/auth/confirm-email')).toHaveLength(1);
      expect(calls.find((c) => c.url === '/api/portal/auth/confirm-email')?.body).toEqual({ userId: 'u1', token: 'tok' });
    });

    it('shows the reason when the link is invalid', async () => {
      mockFetch(csrfThen(() => ({ status: 400, body: { errors: ['This link is invalid or has expired.'] } })));

      renderAt('/confirm-email?userId=u1&token=tok', <ConfirmEmailPage />);

      expect(await screen.findByText('This link is invalid or has expired.')).toBeInTheDocument();
    });
  });

  describe('LoginPage', () => {
    it('links to the password reset', () => {
      mockFetch(() => undefined);

      renderAt('/login', <LoginPage />);

      expect(screen.getByRole('link', { name: 'Forgot your password?' })).toHaveAttribute('href', '/forgot-password');
    });

    it('offers a new confirmation mail when the email is not confirmed yet', async () => {
      const calls = mockFetch(
        csrfThen((call) =>
          call.url === '/api/portal/auth/login'
            ? { status: 403, body: { errors: ['Confirm your email address first.'], error: 'email_not_confirmed' } }
            : { status: 204 },
        ),
      );
      const user = userEvent.setup();
      renderAt('/login', <LoginPage />);

      await user.type(screen.getByLabelText('Email'), 'a@acme.example');
      await user.type(screen.getByLabelText('Password'), 'correct-horse-battery');
      await user.click(screen.getByRole('button', { name: 'Sign in' }));
      await user.click(await screen.findByRole('button', { name: 'Send the confirmation mail again' }));

      expect(await screen.findByText('We sent a new confirmation mail if the address still needs one.')).toBeInTheDocument();
      expect(calls.find((c) => c.url === '/api/portal/auth/resend-confirmation')?.body).toEqual({ email: 'a@acme.example' });
    });
  });
});
