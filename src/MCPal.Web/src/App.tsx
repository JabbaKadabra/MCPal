import { Navigate, Route, Routes } from 'react-router-dom';
import { Layout } from './components/Layout';
import { RequireAuth } from './components/RequireAuth';
import { ConnectionsPage } from './pages/ConnectionsPage';
import { ConnectPage } from './pages/ConnectPage';
import { KeysPage } from './pages/KeysPage';
import { LoginPage } from './pages/LoginPage';
import { OAuthAuthorizePage } from './pages/OAuthAuthorizePage';
import { SignupPage } from './pages/SignupPage';

export function App() {
  return (
    <Routes>
      <Route path="/signup" element={<SignupPage />} />
      <Route path="/login" element={<LoginPage />} />
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
        <Route path="/connect" element={<ConnectPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/keys" replace />} />
    </Routes>
  );
}
