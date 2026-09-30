import { useState } from 'react';
import { t } from '../i18n';

export function CopyButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="ghost small"
      onClick={() => {
        void navigator.clipboard.writeText(value).then(() => setCopied(true));
      }}
    >
      {copied ? t('common.copied') : t('common.copy')}
    </button>
  );
}
