import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes, useLocation } from 'react-router';
import { AuthProvider, useAuth } from './auth';
import { Layout } from './Layout';
import { LoginPage } from './pages/LoginPage';
import { AdminsPage } from './pages/AdminsPage';
import { ApplicationsPage } from './pages/ApplicationsPage';
import { LibraryPage } from './pages/LibraryPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { ProcessingPage } from './pages/ProcessingPage';
import { StoragePage } from './pages/StoragePage';
import { UploadPage } from './pages/UploadPage';
import { WatchPage } from './pages/WatchPage';

function RequireAdmin({ children }: { children: ReactNode }) {
  const { me, loading } = useAuth();
  const location = useLocation();
  if (loading) {
    return <div className="center muted">Loading…</div>;
  }
  if (!me) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  }
  return children;
}

export function App() {
  return (
    <BrowserRouter basename="/admin">
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route
            element={
              <RequireAdmin>
                <Layout />
              </RequireAdmin>
            }
          >
            <Route index element={<LibraryPage />} />
            <Route path="watch/:id" element={<WatchPage />} />
            <Route path="upload" element={<UploadPage />} />
            <Route path="processing" element={<ProcessingPage />} />
            <Route path="applications" element={<ApplicationsPage />} />
            <Route path="storage" element={<StoragePage />} />
            <Route path="admins" element={<AdminsPage />} />
            <Route path="*" element={<NotFoundPage />} />
          </Route>
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  );
}
