import { MantineProvider } from '@mantine/core';
import { render, screen, waitFor } from '@testing-library/react';
import { expect, it } from 'vitest';
import { DecisionConfirmation } from './DecisionConfirmation';

const copy = {
  title: 'Leave logo empty?',
  message: 'The consumer app shows no logo for this title.',
  confirmLabel: 'Leave logo empty',
};

it('keeps its copy while the dialog transitions closed', async () => {
  const dialog = (current: typeof copy | undefined) => (
    <MantineProvider>
      <DecisionConfirmation
        copy={current}
        onCancel={() => {}}
        onConfirm={async () => {}}
        onConfirmed={() => {}}
      />
    </MantineProvider>
  );
  const { rerender } = render(dialog(copy));
  expect(await screen.findByText('Leave logo empty?')).toBeInTheDocument();
  rerender(dialog(undefined));
  expect(screen.getByText('Leave logo empty?')).toBeInTheDocument();
  expect(screen.getByText('The consumer app shows no logo for this title.')).toBeInTheDocument();
  expect(
    screen.getByRole('button', {
      name: 'Leave logo empty',
    }),
  ).toBeInTheDocument();
  await waitFor(() => expect(screen.queryByText('Leave logo empty?')).not.toBeInTheDocument());
});
