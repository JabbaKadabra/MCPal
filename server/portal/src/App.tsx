import { Navigate, Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { RequireAuth } from './components/RequireAuth';
import { AcceptInvitationPage } from './pages/AcceptInvitationPage';
import { AuditPage } from './pages/AuditPage';
import { ConfirmEmailPage } from './pages/ConfirmEmailPage';
import { ConnectionsPage } from './pages/ConnectionsPage';
import { ConnectPage } from './pages/ConnectPage';
import { ForgotPasswordPage } from './pages/ForgotPasswordPage';
import { GroupsPage } from './pages/GroupsPage';
import { KeysPage } from './pages/KeysPage';
import { LoginPage } from './pages/LoginPage';
import { MyAccessPage } from './pages/MyAccessPage';
import { OAuthAuthorizePage } from './pages/OAuthAuthorizePage';
import { ResetPasswordPage } from './pages/ResetPasswordPage';
import { SignupPage } from './pages/SignupPage';
import { UsersPage } from './pages/UsersPage';

export function App() {
  return (
    <Routes>
      <Route path="/signup" element={<SignupPage />} />
      <Route path="/login" element={<LoginPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />
      <Route path="/confirm-email" element={<ConfirmEmailPage />} />
      <Route path="/accept-invitation" element={<AcceptInvitationPage />} />
      <Route path="/oauth/authorize" element={<OAuthAuthorizePage />} />
      <Route
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route path="/keys" element={<KeysPage />} />
        <Route path="/connections" element={<ConnectionsPage />} />
        <Route path="/audit" element={<AuditPage />} />
        <Route path="/users" element={<UsersPage />} />
        <Route path="/groups" element={<GroupsPage />} />
        <Route path="/access" element={<MyAccessPage />} />
        <Route path="/connect" element={<ConnectPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/keys" replace />} />
    </Routes>
  );
}
