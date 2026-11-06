import { Alert, Button, Center, Loader, Stack, Text } from '@mantine/core';
import { Fragment, type ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router';
import { useAuth } from '../../contexts/AuthContext';

interface ProtectedRouteProps {
  children: ReactNode;
  requiredRoles?: string[];
}

export function ProtectedRoute({ children, requiredRoles }: ProtectedRouteProps) {
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
      <Center h="100vh">
        <Stack
          align="center"
          role="alert"
        >
          <Text>{error}</Text>
          <Button onClick={() => void retry()}>Try again</Button>
          <Button
            variant="subtle"
            onClick={() => void logout()}
          >
            Sign out
          </Button>
        </Stack>
      </Center>
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

  if (requiredRoles && requiredRoles.length > 0 && user) {
    const hasRequiredRole = requiredRoles.some((role) => user.roles.includes(role));
    if (!hasRequiredRole) {
      return (
        <Navigate
          to="/"
          replace
        />
      );
    }
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
