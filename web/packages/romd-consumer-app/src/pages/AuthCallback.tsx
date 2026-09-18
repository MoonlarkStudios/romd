import { Center, Loader } from '@mantine/core';
import { useEffect, useRef } from 'react';
import { useNavigate } from 'react-router';
import { userManager } from '../auth/userManager';

/**
 * Completes the OpenIddict authorization-code redirect: exchanges the code for tokens, then
 * navigates back to where the sign-in started. The ref guard keeps StrictMode's double effect
 * from redeeming the single-use code twice.
 */
export function AuthCallback() {
  const navigate = useNavigate();
  const handled = useRef(false);

  useEffect(() => {
    if (handled.current) {
      return;
    }
    handled.current = true;

    userManager
      .signinRedirectCallback()
      .then((user) => {
        const returnTo = (user.state as { returnTo?: string } | undefined)?.returnTo ?? '/';
        navigate(returnTo, { replace: true });
      })
      .catch(() => {
        navigate('/login', {
          replace: true,
          state: { error: 'Sign-in could not be completed. Try again.' },
        });
      });
  }, [navigate]);

  return (
    <Center h="100vh">
      <Loader size="lg" />
    </Center>
  );
}
