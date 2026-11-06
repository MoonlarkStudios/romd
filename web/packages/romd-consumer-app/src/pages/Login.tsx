import { Alert, Button, Loader } from '@mantine/core';
import romdLockup from '@romd/foundation/brand/romd-lockup-horizontal.svg';
import romdLockupDark from '@romd/foundation/brand/romd-lockup-horizontal-dark.svg';
import { useState } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from '../contexts/AuthContext';

export function Login() {
  const { login, isAuthenticated, isLoading } = useAuth();
  const location = useLocation();
  const [isRedirecting, setIsRedirecting] = useState(false);
  const [redirectError, setRedirectError] = useState<string | null>(null);
  const callbackError =
    (
      location.state as {
        error?: string;
      } | null
    )?.error ?? null;
  const error = redirectError ?? callbackError;

  if (isLoading) {
    return (
      <main
        className="romd-auth-shell"
        aria-label="Checking sign-in"
      >
        <Loader
          size="lg"
          color="mint"
        />
      </main>
    );
  }

  if (isAuthenticated) {
    return (
      <Navigate
        to="/"
        replace
      />
    );
  }

  const handleSignIn = async () => {
    setIsRedirecting(true);
    setRedirectError(null);
    try {
      await login();
    } catch {
      setIsRedirecting(false);
      setRedirectError('The sign-in service could not be reached. Try again.');
    }
  };

  return (
    <main className="romd-auth-shell">
      <section
        className="romd-auth-card"
        aria-label="Sign in"
      >
        <h1
          className="romd-auth-brand"
          aria-label="ROMD"
        >
          <img
            className="romd-auth-brand-light"
            src={romdLockup}
            alt=""
          />
          <img
            className="romd-auth-brand-dark"
            src={romdLockupDark}
            alt=""
          />
        </h1>
        <p className="romd-auth-description">Sign in to your library</p>
        {error && (
          <Alert
            color="red"
            role="alert"
          >
            {error}
          </Alert>
        )}
        <Button
          className="romd-auth-submit"
          loading={isRedirecting}
          onClick={handleSignIn}
        >
          Sign in
        </Button>
      </section>
    </main>
  );
}
