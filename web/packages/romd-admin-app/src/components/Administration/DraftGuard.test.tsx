import { MantineProvider } from '@mantine/core';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { type ReactNode, useState } from 'react';
import {
  createMemoryRouter,
  MemoryRouter,
  Outlet,
  RouterProvider,
  useSearchParams,
} from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DraftGuardProvider, useDiscardEpoch, useDraftGuard } from './DraftGuard';
import { UnsavedChanges } from './UnsavedChanges';

const artworkCopy = 'Your saved artwork will stay as it is.';
const matchCopy = 'Your provider match choice has not been confirmed.';

function Editor({
  label,
  description,
  children,
}: {
  label: string;
  description?: string;
  children?: ReactNode;
}) {
  const [draft, setDraft] = useState('');
  return (
    <section>
      <UnsavedChanges
        dirty={draft !== ''}
        description={description}
      />
      <input
        aria-label={label}
        value={draft}
        onChange={(event) => setDraft(event.currentTarget.value)}
      />
      {children}
    </section>
  );
}

function SavingEditor({ label, save }: { label: string; save: Promise<void> }) {
  const [dirty, setDirty] = useState(true);
  return (
    <>
      <UnsavedChanges
        dirty={dirty}
        description={`${label} is being saved.`}
      />
      <button
        type="button"
        onClick={() => void save.then(() => setDirty(false))}
      >
        Save {label}
      </button>
    </>
  );
}

function EpochProbe() {
  return <p>epoch {useDiscardEpoch()}</p>;
}

function NextTitle({
  load,
  onDecided,
}: {
  load: () => Promise<string>;
  onDecided: (discarded: boolean) => void;
}) {
  const [, setParams] = useSearchParams();
  const { discardThen } = useDraftGuard();
  return (
    <button
      type="button"
      onClick={() => {
        void discardThen(async () =>
          setParams({
            tab: await load(),
          }),
        ).then(onDecided);
      }}
    >
      Next title
    </button>
  );
}

function Workspace({
  load = () => Promise.resolve('metadata'),
  onDecided = () => undefined,
}: {
  load?: () => Promise<string>;
  onDecided?: (discarded: boolean) => void;
}) {
  const [params, setParams] = useSearchParams();
  const { hasDraft } = useDraftGuard();
  return (
    <>
      <p>{hasDraft ? 'Draft open' : 'No draft'}</p>
      <button
        type="button"
        onClick={() =>
          setParams({
            tab: 'metadata',
          })
        }
      >
        Metadata tab
      </button>
      <NextTitle
        load={load}
        onDecided={onDecided}
      />
      {(params.get('tab') ?? 'artwork') === 'artwork' ? (
        <Editor
          label="Artwork"
          description={artworkCopy}
        >
          <Editor
            label="Match"
            description={matchCopy}
          />
        </Editor>
      ) : (
        <h1>Metadata editor</h1>
      )}
    </>
  );
}

type Task = (go: (step: string) => void) => unknown;

function Steps({ tasks, onSettled }: { tasks: Task[]; onSettled: (outcome: string) => void }) {
  const [, setParams] = useSearchParams();
  const { discardThen } = useDraftGuard();
  const go = (step: string) =>
    setParams({
      step,
    });
  return (
    <>
      <Editor
        label="Artwork"
        description={artworkCopy}
      />
      <button
        type="button"
        onClick={() => {
          for (const task of tasks) {
            void discardThen(() => task(go)).then(
              (result) => onSettled(String(result)),
              (error: Error) => onSettled(error.message),
            );
          }
        }}
      >
        Run tasks
      </button>
      <button
        type="button"
        onClick={() => go('later')}
      >
        Later step
      </button>
    </>
  );
}

/** An editor that a same-page navigation keeps mounted, keyed on the discard epoch. */
function SamePage() {
  const epoch = useDiscardEpoch();
  const [params, setParams] = useSearchParams();
  const { hasDraft, discardThen } = useDraftGuard();
  return (
    <>
      <p>
        {hasDraft ? 'Draft open' : 'No draft'} · epoch {epoch} · step{' '}
        {params.get('step') ?? 'first'}
      </p>
      <Editor
        key={epoch}
        label="Settings"
      />
      <button
        type="button"
        onClick={() =>
          setParams({
            step: 'second',
          })
        }
      >
        Second step
      </button>
      <button
        type="button"
        onClick={() =>
          setParams({
            step: 'third',
          })
        }
      >
        Third step
      </button>
      <button
        type="button"
        onClick={() => void discardThen(() => undefined)}
      >
        Discard without navigating
      </button>
    </>
  );
}

async function discardAndRun(tasks: Task[]) {
  const onSettled = vi.fn();
  const router = mount(
    <Steps
      tasks={tasks}
      onSettled={onSettled}
    />,
  );
  await userEvent.type(
    screen.getByRole('textbox', {
      name: 'Artwork',
    }),
    'Cropped poster',
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Run tasks',
    }),
  );
  await userEvent.click(
    screen.getByRole('button', {
      name: 'Discard',
    }),
  );
  return {
    router,
    onSettled,
  };
}

function mount(
  element: ReactNode,
  options: {
    initialEntries?: string[];
    initialIndex?: number;
    guarded?: boolean;
  } = {},
) {
  const {
    initialEntries = [
      '/',
    ],
    initialIndex,
    guarded = true,
  } = options;
  const pages = [
    {
      path: '/',
      element,
    },
    {
      path: '/first',
      element: <h1>First page</h1>,
    },
    {
      path: '/last',
      element: <h1>Last page</h1>,
    },
  ];
  const router = createMemoryRouter(
    guarded
      ? [
          {
            element: (
              <DraftGuardProvider>
                <Outlet />
              </DraftGuardProvider>
            ),
            children: pages,
          },
        ]
      : pages,
    {
      initialEntries,
      initialIndex,
    },
  );
  render(
    <MantineProvider env="test">
      <RouterProvider router={router} />
    </MantineProvider>,
  );
  return router;
}

afterEach(() => vi.restoreAllMocks());

describe('draft guard navigation', () => {
  it('continues a blocked navigation when the last pending save finishes without discarding drafts', async () => {
    let finishMatch: () => void = () => undefined;
    let finishArtwork: () => void = () => undefined;
    const match = new Promise<void>((resolve) => {
      finishMatch = resolve;
    });
    const artwork = new Promise<void>((resolve) => {
      finishArtwork = resolve;
    });
    const router = mount(
      <>
        <SavingEditor
          label="Match"
          save={match}
        />
        <SavingEditor
          label="Artwork"
          save={artwork}
        />
        <Workspace />
        <EpochProbe />
      </>,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save Match',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save Artwork',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Metadata tab',
      }),
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    await act(async () => finishMatch());
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(screen.getByText('Artwork is being saved.')).toBeInTheDocument();
    expect(router.state.location.search).toBe('');

    await act(async () => finishArtwork());
    await waitFor(() => expect(router.state.location.search).toBe('?tab=metadata'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByText('epoch 0')).toBeInTheDocument();
  });

  it('shows one dialog for nested dirty drafts and keeps both when editing continues', async () => {
    const warn = vi.spyOn(console, 'warn');
    const router = mount(<Workspace />);
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Match',
      }),
      'Chrono Trigger',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Metadata tab',
      }),
    );
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText(artworkCopy)).toBeInTheDocument();
    expect(screen.getByText(matchCopy)).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(router.state.location.search).toBe('');
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
    ).toHaveValue('Cropped poster');
    expect(
      screen.getByRole('textbox', {
        name: 'Match',
      }),
    ).toHaveValue('Chrono Trigger');
    expect(warn).not.toHaveBeenCalledWith(expect.stringContaining('only supports one blocker'));
  });

  it('discards every draft and lets the blocked navigation through exactly once', async () => {
    const router = mount(<Workspace />, {
      initialEntries: [
        '/?tab=artwork',
      ],
    });
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Match',
      }),
      'Chrono Trigger',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Metadata tab',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Metadata editor',
      }),
    ).toBeInTheDocument();
    expect(router.state.location.search).toBe('?tab=metadata');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByText('No draft')).toBeInTheDocument();
    await act(async () => {
      await router.navigate(-1);
    });
    expect(router.state.location.search).toBe('?tab=artwork');
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
    ).toHaveValue('');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('guards browser Back and Forward', async () => {
    const router = mount(<Workspace />, {
      initialEntries: [
        '/first',
        '/',
        '/last',
      ],
      initialIndex: 1,
    });
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await act(async () => {
      await router.navigate(-1);
    });
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.pathname).toBe('/');
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
    ).toHaveValue('Cropped poster');
    await act(async () => {
      await router.navigate(1);
    });
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Last page',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('registers the beforeunload prompt only while a draft is dirty', async () => {
    const listen = vi.spyOn(window, 'addEventListener');
    const unlisten = vi.spyOn(window, 'removeEventListener');
    const unload = () => {
      const event = new Event('beforeunload', {
        cancelable: true,
      });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    const beforeUnloadCalls = (spy: typeof listen) =>
      spy.mock.calls.filter(([type]) => type === 'beforeunload');
    mount(<Workspace />);
    expect(beforeUnloadCalls(listen)).toHaveLength(0);
    expect(unload()).toBe(false);
    const artwork = screen.getByRole('textbox', {
      name: 'Artwork',
    });
    await userEvent.type(artwork, 'Cropped poster');
    expect(beforeUnloadCalls(listen)).toHaveLength(1);
    expect(unload()).toBe(true);
    await userEvent.clear(artwork);
    expect(beforeUnloadCalls(unlisten)).toHaveLength(1);
    expect(unload()).toBe(false);
  });

  it('keeps guarding a data-router page that has no provider', async () => {
    function SettingsPage() {
      const [, setParams] = useSearchParams();
      return (
        <>
          <Editor
            label="Artwork"
            description={artworkCopy}
          />
          <button
            type="button"
            onClick={() =>
              setParams({
                tab: 'metadata',
              })
            }
          >
            Metadata tab
          </button>
        </>
      );
    }
    const router = mount(<SettingsPage />, {
      guarded: false,
    });
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Metadata tab',
      }),
    );
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText(artworkCopy)).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.search).toBe('');
  });

  it('renders nothing and never blocks without a data router', async () => {
    const onDecided = vi.fn();
    render(
      <MantineProvider env="test">
        <MemoryRouter>
          <DraftGuardProvider>
            <Workspace onDecided={onDecided} />
          </DraftGuardProvider>
        </MemoryRouter>
      </MantineProvider>,
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    expect(screen.getByText('No draft')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    expect(
      await screen.findByRole('heading', {
        name: 'Metadata editor',
      }),
    ).toBeInTheDocument();
    await waitFor(() => expect(onDecided).toHaveBeenCalledWith(true));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});

describe('discard epoch', () => {
  it('drops a draft that the discarded navigation keeps mounted, so no second dialog ever appears', async () => {
    const router = mount(<SamePage />);
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
      'New name',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Second step',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(screen.getByText('Draft open · epoch 0 · step first')).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
    ).toHaveValue('New name');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Second step',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('No draft · epoch 1 · step second')).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
    ).toHaveValue('');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Third step',
      }),
    );
    await waitFor(() => expect(router.state.location.search).toBe('?step=third'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('advances when discardThen is discarded, never when the curator keeps editing', async () => {
    mount(<SamePage />);
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
      'New name',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard without navigating',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(screen.getByText('Draft open · epoch 0 · step first')).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard without navigating',
      }),
    );
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    expect(await screen.findByText('No draft · epoch 1 · step first')).toBeInTheDocument();
    expect(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
    ).toHaveValue('');
  });

  it('returns focus to the control that tried to leave only when editing continues', async () => {
    mount(<SamePage />);
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Settings',
      }),
      'New name',
    );
    const leave = screen.getByRole('button', {
      name: 'Second step',
    });
    await userEvent.click(leave);
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Keep editing',
      }),
    );
    await waitFor(() => expect(leave).toHaveFocus());
    await userEvent.click(leave);
    await userEvent.click(
      await screen.findByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
    expect(leave).not.toHaveFocus();
  });

  it('stays at zero without a provider', () => {
    function Probe() {
      return <p>epoch {useDiscardEpoch()}</p>;
    }
    render(
      <MemoryRouter>
        <Probe />
      </MemoryRouter>,
    );
    expect(screen.getByText('epoch 0')).toBeInTheDocument();
  });
});

describe('discardThen', () => {
  it('runs a waiting task when a pending save clears the last draft without counting as a discard', async () => {
    let finish: () => void = () => undefined;
    const save = new Promise<void>((resolve) => {
      finish = resolve;
    });
    const onDecided = vi.fn();
    const router = mount(
      <>
        <SavingEditor
          label="Match"
          save={save}
        />
        <Workspace onDecided={onDecided} />
        <EpochProbe />
      </>,
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Save Match',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(onDecided).not.toHaveBeenCalled();

    await act(async () => finish());
    await waitFor(() => expect(onDecided).toHaveBeenCalledWith(true));
    expect(router.state.location.search).toBe('?tab=metadata');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByText('epoch 0')).toBeInTheDocument();
  });

  it('runs the task and resolves true when no draft is open', async () => {
    const onDecided = vi.fn();
    const router = mount(<Workspace onDecided={onDecided} />);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(router.state.location.search).toBe('?tab=metadata'));
    await waitFor(() => expect(onDecided).toHaveBeenCalledWith(true));
  });

  it('resolves false after Keep editing and never runs the task', async () => {
    const onDecided = vi.fn();
    const load = vi.fn(() => Promise.resolve('metadata'));
    const router = mount(
      <Workspace
        load={load}
        onDecided={onDecided}
      />,
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    await waitFor(() => expect(onDecided).toHaveBeenCalledWith(false));
    expect(load).not.toHaveBeenCalled();
    expect(router.state.location.search).toBe('');
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
    ).toHaveValue('Cropped poster');
  });

  it('lets the navigation at the end of async work through after Discard without a second dialog', async () => {
    const onDecided = vi.fn();
    let finishLoad: (tab: string) => void = () => undefined;
    const load = () =>
      new Promise<string>((resolve) => {
        finishLoad = resolve;
      });
    const router = mount(
      <Workspace
        load={load}
        onDecided={onDecided}
      />,
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Match',
      }),
      'Chrono Trigger',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Next title',
      }),
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(onDecided).not.toHaveBeenCalled();
    await act(async () => finishLoad('metadata'));
    expect(
      await screen.findByRole('heading', {
        name: 'Metadata editor',
      }),
    ).toBeInTheDocument();
    expect(router.state.location.search).toBe('?tab=metadata');
    expect(onDecided).toHaveBeenCalledWith(true);
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByText('No draft')).toBeInTheDocument();
  });

  it('passes exactly one navigation inside the task', async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const { router, onSettled } = await discardAndRun([
      async (go) => {
        go('first');
        await gate;
        go('second');
      },
    ]);
    await waitFor(() => expect(router.state.location.search).toBe('?step=first'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await act(async () => release());
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    expect(router.state.location.search).toBe('?step=first');
    expect(onSettled).toHaveBeenCalledWith('true');
  });

  it('clears the allowance when the task settles without navigating', async () => {
    const { router, onSettled } = await discardAndRun([
      () => Promise.resolve(),
    ]);
    await waitFor(() => expect(onSettled).toHaveBeenCalledWith('true'));
    expect(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
    ).toHaveValue('Cropped poster');
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Later step',
      }),
    );
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    expect(router.state.location.search).toBe('');
  });

  it('clears the allowance and rejects when the task fails', async () => {
    const { router, onSettled } = await discardAndRun([
      () => Promise.reject(new Error('Page fetch failed')),
    ]);
    await waitFor(() => expect(onSettled).toHaveBeenCalledWith('Page fetch failed'));
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Later step',
      }),
    );
    expect(
      screen.getByRole('dialog', {
        name: 'Discard unsaved changes?',
      }),
    ).toBeInTheDocument();
    expect(router.state.location.search).toBe('');
  });

  it('shares one dialog and one decision between concurrent calls', async () => {
    const first = vi.fn();
    const second = vi.fn();
    const onSettled = vi.fn();
    mount(
      <Steps
        tasks={[
          first,
          second,
        ]}
        onSettled={onSettled}
      />,
    );
    await userEvent.type(
      screen.getByRole('textbox', {
        name: 'Artwork',
      }),
      'Cropped poster',
    );
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Run tasks',
      }),
    );
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Keep editing',
      }),
    );
    await waitFor(() => expect(onSettled).toHaveBeenCalledTimes(2));
    expect(onSettled.mock.calls).toEqual([
      [
        'false',
      ],
      [
        'false',
      ],
    ]);
    expect(first).not.toHaveBeenCalled();
    expect(second).not.toHaveBeenCalled();
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Run tasks',
      }),
    );
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    await userEvent.click(
      screen.getByRole('button', {
        name: 'Discard',
      }),
    );
    await waitFor(() => expect(onSettled).toHaveBeenCalledTimes(4));
    expect(onSettled.mock.calls.slice(2)).toEqual([
      [
        'true',
      ],
      [
        'true',
      ],
    ]);
    expect(first).toHaveBeenCalledOnce();
    expect(second).toHaveBeenCalledOnce();
  });
});
