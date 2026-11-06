import { MantineProvider, useMantineEnv } from '@mantine/core';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ConsumerPresentation } from './ConsumerPresentation';

function EnvironmentProbe() {
  return <span>{useMantineEnv()}</span>;
}

describe('ConsumerPresentation', () => {
  it('keeps the host provider environment', () => {
    render(
      <MantineProvider env="test">
        <ConsumerPresentation>
          <EnvironmentProbe />
        </ConsumerPresentation>
      </MantineProvider>,
    );

    expect(screen.getByText('test')).toBeInTheDocument();
  });

  it('uses the default environment without a host provider', () => {
    render(
      <ConsumerPresentation>
        <EnvironmentProbe />
      </ConsumerPresentation>,
    );

    expect(screen.getByText('default')).toBeInTheDocument();
  });
});
