import { MantineProvider } from '@mantine/core';
import type { ReactNode } from 'react';
import { consumerTheme } from './theme/consumerTheme';

/** Isolates the consumer palette and responsive container from the host application. */
export function ConsumerPresentation({ children }: { children: ReactNode }) {
  return <MantineProvider theme={consumerTheme} forceColorScheme="dark" cssVariablesSelector=".romd-consumer-presentation" getRootElement={() => undefined} withGlobalClasses={false}>
    <div className="romd-consumer-presentation" data-mantine-color-scheme="dark">{children}</div>
  </MantineProvider>;
}
