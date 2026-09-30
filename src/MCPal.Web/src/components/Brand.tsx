import { t } from '../i18n';

/** The relay mark: the agent side (orange) joined to the Claude side (blue), then the wordmark. */
export function Brand() {
  return (
    <span className="brand">
      <svg className="brand-mark" viewBox="0 0 44 24" aria-hidden="true" focusable="false">
        <rect x="1.5" y="1.5" width="15" height="21" fill="var(--agent)" stroke="currentColor" strokeWidth="3" />
        <rect x="27.5" y="1.5" width="15" height="21" fill="var(--claude)" stroke="currentColor" strokeWidth="3" />
        <path d="M16.5 12h11" stroke="currentColor" strokeWidth="4" />
      </svg>
      <span className="brand-name">{t('app.name')}</span>
    </span>
  );
}
