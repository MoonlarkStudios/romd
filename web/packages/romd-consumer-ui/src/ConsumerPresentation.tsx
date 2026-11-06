import { MantineContext, MantineProvider } from '@mantine/core';
import { darkCssVariablesResolver } from '@romd/foundation/theme';
import { type ReactNode, use } from 'react';
import { consumerTheme } from './theme/consumerTheme';

/** Isolates the consumer palette and responsive container from the host application. */
export function ConsumerPresentation({ children }: { children: ReactNode }) {
  // A nested provider would otherwise reset the host environment, such as test mode.
  const hostEnvironment = use(MantineContext)?.env;

  return (
    <MantineProvider
      env={hostEnvironment}
      cssVariablesResolver={darkCssVariablesResolver}
      theme={consumerTheme}
      forceColorScheme="dark"
      cssVariablesSelector=".romd-consumer-presentation"
      getRootElement={() => undefined}
      withGlobalClasses={false}
    >
      <div
        className="romd-consumer-presentation"
        data-mantine-color-scheme="dark"
      >
        {children}
      </div>
    </MantineProvider>
  );
}
