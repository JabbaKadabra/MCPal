import type { ReactNode } from 'react';
import { Navigate } from 'react-router-dom';
import { t } from '../i18n';
import { useMe } from './useMe';

export function RequireAuth({ children }: { children: ReactNode }) {
  const me = useMe();
  if (me.isPending) {
    return <p className="center muted">{t('common.loading')}</p>;
  }
  if (me.data === null || me.isError) {
    return <Navigate to="/login" replace />;
  }
  return <>{children}</>;
}
