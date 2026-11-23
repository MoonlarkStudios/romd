import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import {
  createContext,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
} from 'react';
import { UNSAFE_DataRouterContext, useBlocker } from 'react-router';

interface Draft {
  description: string;
  saving: boolean;
}

type Drafts = ReadonlyMap<string, Draft>;

export interface DraftGuard {
  hasDraft: boolean;
  discardThen: (task: () => unknown) => Promise<boolean>;
  /** Allows a synchronous URL update for a control that keeps the same draft mounted. */
  navigateWithinEditor: (navigate: () => void) => void;
}

interface DraftRegistry extends DraftGuard {
  register: (id: string, draft: Draft) => () => void;
  discardEpoch: number;
}

const DraftGuardContext = createContext<DraftRegistry | null>(null);
const unguarded: DraftGuard = {
  hasDraft: false,
  navigateWithinEditor: (navigate) => navigate(),
  discardThen: async (task) => {
    await task();
    return true;
  },
};

/**
 * Owns the router's only blocker, the `beforeunload` prompt and the discard dialog for every draft below it.
 * Mount once inside the data router; nested providers defer to the outer one, and without a data router
 * children render unguarded.
 */
export function DraftGuardProvider({ children }: { children: ReactNode }) {
  const outer = useContext(DraftGuardContext);
  const router = useContext(UNSAFE_DataRouterContext);
  return outer || !router ? children : <RouterDraftGuard>{children}</RouterDraftGuard>;
}

/**
 * `discardThen(task)` runs `task` when nothing is dirty, or after the curator chooses Discard, and resolves true once
 * it settles (rejecting with the task's error). Keep editing resolves false without running it. While a discarded
 * task is pending, one navigation passes the blocker; the allowance ends when the task settles, navigated or not.
 * Calls made while the dialog is open share its decision: after Discard each task runs with its own allowance.
 * Await numeric (Back/Forward) navigations inside the task, because history pops are checked asynchronously.
 */
export function useDraftGuard(): DraftGuard {
  return useContext(DraftGuardContext) ?? unguarded;
}

/**
 * Increments each time the curator chooses Discard. A navigation that keeps an editor mounted (same title, same
 * library) would otherwise keep its draft; editors that can survive one key their draft state on this.
 */
export function useDiscardEpoch(): number {
  return useContext(DraftGuardContext)?.discardEpoch ?? 0;
}

export function useDraft(dirty: boolean, description: string, saving = false) {
  const id = useId();
  const register = useContext(DraftGuardContext)?.register;
  useEffect(
    () =>
      register && (dirty || saving)
        ? register(id, {
            description,
            saving,
          })
        : undefined,
    [
      dirty,
      description,
      id,
      register,
      saving,
    ],
  );
}

function RouterDraftGuard({ children }: { children: ReactNode }) {
  const live = useRef<Drafts>(new Map());
  const [drafts, setDrafts] = useState<Drafts>(live.current);
  const passes = useRef<ReadonlySet<symbol>>(new Set());
  const waiting = useRef<readonly ((discard: boolean) => void)[]>([]);
  const [confirming, setConfirming] = useState(false);
  const [discardEpoch, setDiscardEpoch] = useState(0);
  // Keep editing returns focus to the control that tried to leave; after Discard the destination owns focus.
  const [discarded, setDiscarded] = useState(false);
  const settledNavigation = useRef<(() => void) | undefined>(undefined);
  const blocker = useBlocker(
    useCallback(() => {
      if (live.current.size === 0) return false;
      const [pass] = passes.current;
      if (pass === undefined) return true;
      passes.current = withoutPass(passes.current, pass);
      return false;
    }, []),
  );
  const hasDraft = drafts.size > 0;
  const registeredDrafts = Array.from(drafts.values());
  const saving = registeredDrafts.some((draft) => draft.saving);

  // A pending save can clear the last draft after navigation was blocked. Continue the
  // requested action without asking to discard changes that have already been saved.
  useEffect(() => {
    if (hasDraft || live.current.size > 0) return;
    if (blocker.state === 'blocked' && settledNavigation.current !== blocker.proceed) {
      settledNavigation.current = blocker.proceed;
      setDiscarded(true);
      blocker.proceed();
    }
    if (confirming) {
      const waiters = waiting.current;
      waiting.current = [];
      setConfirming(false);
      setDiscarded(true);
      for (const resolve of waiters) resolve(true);
    }
  }, [
    hasDraft,
    blocker,
    confirming,
  ]);

  useEffect(() => {
    if (!hasDraft) return;
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [
    hasDraft,
  ]);

  const register = useCallback((id: string, draft: Draft) => {
    const publish = (next: Drafts) => {
      live.current = next;
      setDrafts(next);
    };
    publish(new Map(live.current).set(id, draft));
    return () => {
      const next = new Map(live.current);
      next.delete(id);
      publish(next);
    };
  }, []);

  const navigateWithinEditor = useCallback((navigate: () => void) => {
    const pass = Symbol('navigation within an editor');
    passes.current = new Set(passes.current).add(pass);
    try {
      navigate();
    } finally {
      passes.current = withoutPass(passes.current, pass);
    }
  }, []);

  const discardThen = useCallback(async (task: () => unknown) => {
    if (live.current.size === 0) {
      await task();
      return true;
    }
    const discard = await new Promise<boolean>((resolve) => {
      waiting.current = [
        ...waiting.current,
        resolve,
      ];
      setConfirming(true);
    });
    if (!discard) return false;
    const pass = Symbol('discarded draft navigation');
    passes.current = new Set(passes.current).add(pass);
    try {
      await task();
    } finally {
      passes.current = withoutPass(passes.current, pass);
    }
    return true;
  }, []);

  const settle = (discard: boolean) => {
    const blocked = blocker.state === 'blocked';
    if (blocked) {
      settledNavigation.current = blocker.proceed;
      if (discard) blocker.proceed();
      else blocker.reset();
    }
    const waiters = waiting.current;
    setDiscarded(discard);
    if (discard && (blocked || waiters.length > 0)) setDiscardEpoch((epoch) => epoch + 1);
    if (waiters.length === 0) return;
    waiting.current = [];
    setConfirming(false);
    for (const resolve of waiters) resolve(discard);
  };

  const guard = useMemo(
    () => ({
      hasDraft,
      discardThen,
      navigateWithinEditor,
      register,
      discardEpoch,
    }),
    [
      hasDraft,
      discardThen,
      navigateWithinEditor,
      register,
      discardEpoch,
    ],
  );
  const descriptions = [
    ...new Set(registeredDrafts.map((draft) => draft.description)),
  ];
  return (
    <DraftGuardContext.Provider value={guard}>
      {children}
      <Modal
        opened={blocker.state === 'blocked' || confirming}
        onClose={() => settle(false)}
        title={saving ? 'Saving your changes' : 'Discard unsaved changes?'}
        centered
        returnFocus={!discarded}
      >
        <Stack>
          {saving ? (
            <Text>The save is already in progress. You can stay here to see the result.</Text>
          ) : (
            descriptions.map((description) => <Text key={description}>{description}</Text>)
          )}
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => settle(false)}
            >
              {saving ? 'Stay here' : 'Keep editing'}
            </Button>
            {!saving && (
              <Button
                color="red"
                onClick={() => settle(true)}
              >
                Discard
              </Button>
            )}
          </Group>
        </Stack>
      </Modal>
    </DraftGuardContext.Provider>
  );
}

function withoutPass(passes: ReadonlySet<symbol>, pass: symbol): ReadonlySet<symbol> {
  const next = new Set(passes);
  next.delete(pass);
  return next;
}
