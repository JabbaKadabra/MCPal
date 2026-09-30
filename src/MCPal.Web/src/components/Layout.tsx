import { useMutation, useQueryClient } from '@tanstack/react-query';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { t } from '../i18n';
import { meQueryKey, useMe } from './useMe';

export function Layout() {
  const me = useMe();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const signOut = useMutation({
    mutationFn: api.logout,
    onSuccess: async () => {
      queryClient.clear();
      queryClient.setQueryData(meQueryKey, null);
      await navigate('/login');
    },
  });

  return (
    <div className="shell">
      <header className="topbar">
        <strong className="brand">{t('app.name')}</strong>
        <nav aria-label="Main">
          <NavLink to="/keys">{t('nav.keys')}</NavLink>
          <NavLink to="/connections">{t('nav.connections')}</NavLink>
          <NavLink to="/audit">{t('nav.audit')}</NavLink>
          {me.data?.role === 'owner' && <NavLink to="/users">{t('nav.users')}</NavLink>}
          <NavLink to="/connect">{t('nav.connect')}</NavLink>
        </nav>
        <span className="spacer" />
        <span className="muted who">{me.data?.companyName}</span>
        <button type="button" className="ghost" onClick={() => signOut.mutate()}>
          {t('nav.signOut')}
        </button>
      </header>
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
