import { useEffect } from 'react';
import { Navigate, useLocation } from 'react-router';
import { LOGIN_PATH, useSession } from './SessionProvider';

/** Signed out on a console page: keep that page in memory (FR-057, never in the URL) and show the login page. */
export function RedirectToLogin() {
  const { rememberReturnTo } = useSession();
  const location = useLocation();
  const path = location.pathname + location.search;

  useEffect(() => {
    rememberReturnTo(path);
  }, [path, rememberReturnTo]);

  return <Navigate to={LOGIN_PATH} replace />;
}
