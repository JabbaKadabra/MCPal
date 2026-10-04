import { useQuery } from '@tanstack/react-query';
import { Navigate } from 'react-router-dom';
import { api } from '../api/client';
import { useMe } from '../components/useMe';
import { t } from '../i18n';

/**
 * Where signing in lands. An owner whose company never had a bridge connected gets the setup walkthrough; everyone else, and
 * owners once a bridge has connected, get the API keys. If the setup state cannot be read the owner lands on the keys too.
 */
export function Home() {
  const me = useMe();
  const isOwner = me.data?.role === 'owner';
  const setup = useQuery({ queryKey: ['setup'], queryFn: api.setup, enabled: isOwner });

  if (me.isPending || (isOwner && setup.isPending)) {
    return <p className="muted">{t('common.loading')}</p>;
  }
  return <Navigate to={isOwner && setup.data?.hasConnectedBridge === false ? '/setup' : '/keys'} replace />;
}
