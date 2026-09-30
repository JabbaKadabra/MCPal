import type { ReactNode } from 'react';
import { t } from '../i18n';
import { Brand } from './Brand';

/** Frame of the signed-out pages and the OAuth sign-in page: product slab beside the form card. */
export function AuthFrame({ children }: { children: ReactNode }) {
  return (
    <div className="auth">
      <aside className="auth-slab">
        <Brand />
        <p className="auth-tagline">{t('app.tagline')}</p>
      </aside>
      <main className="auth-main">
        <div className="card auth-card">{children}</div>
      </main>
    </div>
  );
}
