import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { t } from '../i18n';
import { Brand } from './Brand';
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
        <Link to="/keys" className="topbar-home">
          <Brand />
        </Link>
        <nav aria-label="Main">
          <NavLink to="/keys">{t('nav.keys')}</NavLink>
          <NavLink to="/connections">{t('nav.connections')}</NavLink>
          <NavLink to="/audit">{t('nav.audit')}</NavLink>
          {me.data?.role === 'owner' && <NavLink to="/users">{t('nav.users')}</NavLink>}
          {me.data?.role === 'owner' && <NavLink to="/groups">{t('nav.groups')}</NavLink>}
          <NavLink to="/access">{t('nav.access')}</NavLink>
          <NavLink to="/connect">{t('nav.connect')}</NavLink>
        </nav>
        <span className="spacer" />
        <span className="who">{me.data?.companyName}</span>
        <button type="button" className="ghost on-dark" onClick={() => signOut.mutate()}>
          {t('nav.signOut')}
        </button>
      </header>
      <div className="cable" aria-hidden="true" />
      <main className="content">
        <Outlet />
      </main>
    </div>
  );
}
