import { Alert, Button, Center, Loader } from '@mantine/core';
import { Fragment, type ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';
import { StatusState } from '../status/StatusState';

interface ProtectedRouteProps {
  children: ReactNode;
}

export function ProtectedRoute({ children }: ProtectedRouteProps) {
  const { isAuthenticated, isLoading, user, error, retry, logout } = useAuth();
  const location = useLocation();

  if (isLoading) {
    return (
      <Center h="100vh">
        <Loader size="lg" />
      </Center>
    );
  }

  if (error && !user) {
    return (
      <StatusState
        kind="error"
        message={error}
        onRetry={() => void retry()}
        action={
          <Button
            variant="subtle"
            onClick={() => void logout()}
          >
            Sign out
          </Button>
        }
      />
    );
  }

  if (!isAuthenticated) {
    return (
      <Navigate
        to="/login"
        state={{
          from: location.pathname,
        }}
        replace
      />
    );
  }

  return (
    <>
      {error && (
        <Alert
          title="Account refresh failed"
          role="alert"
        >
          {error}
          <Button
            variant="subtle"
            onClick={() => void retry()}
          >
            Try again
          </Button>
        </Alert>
      )}
      <Fragment key="protected-content">{children}</Fragment>
    </>
  );
}
