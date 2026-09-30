import { ApiError } from '../api/client';
import { t } from '../i18n';

export function ErrorText({ error }: { error: unknown }) {
  if (error === null || error === undefined) {
    return null;
  }
  const messages = error instanceof ApiError ? error.errors : [t('common.error')];
  return (
    <ul className="errors" role="alert">
      {messages.map((message) => (
        <li key={message}>{message}</li>
      ))}
    </ul>
  );
}
