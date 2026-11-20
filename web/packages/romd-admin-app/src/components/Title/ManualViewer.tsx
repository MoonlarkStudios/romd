import {
  ActionIcon,
  Alert,
  Box,
  Button,
  Group,
  Loader,
  Modal,
  SegmentedControl,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core';
import { useElementSize, useMediaQuery } from '@mantine/hooks';
import { IconChevronLeft, IconChevronRight, IconDownload, IconFileText } from '@tabler/icons-react';
import { type KeyboardEvent, useEffect, useRef, useState } from 'react';
import { workspaceActionProps } from '../Workspace/workspaceActions';
import classes from './ArtworkWorkspace.module.css';
import { type ManualDocument, openManual, spreadPages, stepPage } from './manualDocument';

type ManualState =
  | {
      status: 'loading';
    }
  | {
      status: 'ready';
      document: ManualDocument;
    }
  | {
      status: 'failed';
    };

/** Opens the manual while `enabled`, and releases pdf.js's copy and worker when it closes or changes. */
function useManual(titleId: string, mediaId: string, enabled: boolean, preview: boolean) {
  const [state, setState] = useState<ManualState>({
    status: 'loading',
  });
  const [attempt, setAttempt] = useState(0);
  // biome-ignore lint/correctness/useExhaustiveDependencies: attempt triggers an explicit retry of a failed load.
  useEffect(() => {
    if (!enabled) return;
    let active = true;
    let release: (() => void) | undefined;
    setState({
      status: 'loading',
    });
    void openManual(titleId, mediaId, {
      preview,
    })
      .then(({ promise, cancel }) => {
        release = cancel;
        if (!active) cancel();
        return promise;
      })
      .then(
        (document) => {
          if (active)
            setState({
              status: 'ready',
              document,
            });
        },
        () => {
          if (active)
            setState({
              status: 'failed',
            });
        },
      );
    return () => {
      active = false;
      release?.();
    };
  }, [
    titleId,
    mediaId,
    enabled,
    preview,
    attempt,
  ]);
  return {
    state,
    retry: () => setAttempt((value) => value + 1),
  };
}

/** Renders one page to a canvas at the given CSS width, sharp on high-density displays. */
function ManualPageCanvas({
  document,
  page,
  width,
  label,
}: {
  document: ManualDocument;
  page: number;
  width: number;
  label: string;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const target = canvas.current;
    if (!target || width <= 0) return;
    let cancelled = false;
    let cancelRender: (() => void) | undefined;
    setFailed(false);
    void document
      .getPage(page)
      .then(async (proxy) => {
        if (cancelled) return;
        const unscaled = proxy.getViewport({
          scale: 1,
        });
        const ratio = window.devicePixelRatio || 1;
        const viewport = proxy.getViewport({
          scale: (width / unscaled.width) * ratio,
        });
        target.width = Math.floor(viewport.width);
        target.height = Math.floor(viewport.height);
        target.style.width = `${width}px`;
        target.style.height = `${Math.floor(viewport.height / ratio)}px`;
        const context = target.getContext('2d');
        if (!context) return;
        const task = proxy.render({
          canvas: target,
          canvasContext: context,
          viewport,
        });
        cancelRender = () => task.cancel();
        await task.promise;
      })
      .catch((reason: unknown) => {
        if (
          !cancelled &&
          !(reason instanceof Error && reason.name === 'RenderingCancelledException')
        )
          setFailed(true);
      });
    return () => {
      cancelled = true;
      cancelRender?.();
    };
  }, [
    document,
    page,
    width,
  ]);
  return failed ? (
    <Text
      size="sm"
      c="dimmed"
    >
      Page {page} could not be displayed.
    </Text>
  ) : (
    <canvas
      ref={canvas}
      role="img"
      aria-label={label}
      className={classes.manualPage}
    />
  );
}

/** The manual's first page, fetched with range requests so the rest of the document is not downloaded. */
export function ManualCover({
  titleId,
  mediaId,
  width,
  label,
}: {
  titleId: string;
  mediaId: string;
  width: number;
  label: string;
}) {
  const { state } = useManual(titleId, mediaId, true, true);
  if (state.status === 'ready')
    return (
      <ManualPageCanvas
        document={state.document}
        page={1}
        width={width}
        label={label}
      />
    );
  return (
    <Stack
      align="center"
      justify="center"
      gap={4}
      className={classes.manualPlaceholder}
      aria-label={state.status === 'loading' ? `Loading ${label}` : label}
      role="img"
    >
      <IconFileText size={28} />
      {state.status === 'failed' && (
        <Text
          size="xs"
          c="dimmed"
        >
          Preview unavailable
        </Text>
      )}
    </Stack>
  );
}

/**
 * Reads a manual page by page or as facing spreads. Arrow, Page Up/Down, Home and End keys move through it, and the
 * download is the document pdf.js already holds.
 */
export function ManualViewer({
  titleId,
  mediaId,
  title,
  fileName,
  opened,
  onClose,
}: {
  titleId: string;
  mediaId: string;
  title: string;
  fileName: string;
  opened: boolean;
  onClose: () => void;
}) {
  const phone = useMediaQuery('(max-width: 48em)');
  const { state, retry } = useManual(titleId, mediaId, opened, false);
  const [page, setPage] = useState(1);
  const [layout, setLayout] = useState<'single' | 'spread'>('single');
  const [downloading, setDownloading] = useState(false);
  const { ref: stage, width: stageWidth } = useElementSize();
  const pageCount = state.status === 'ready' ? state.document.numPages : 0;
  const spread = layout === 'spread' && !phone;
  const shown = spread
    ? spreadPages(page, pageCount)
    : [
        page,
      ];
  const move = (direction: 1 | -1) =>
    setPage((value) => stepPage(value, pageCount, spread, direction));
  useEffect(() => {
    if (!opened) setPage(1);
  }, [
    opened,
  ]);
  const onKeyDown = (event: KeyboardEvent) => {
    if (
      !pageCount ||
      (event.target instanceof HTMLElement &&
        event.target.closest('input, select, textarea, [role="radio"], [role="radiogroup"]'))
    )
      return;
    const handled: Record<string, () => void> = {
      ArrowRight: () => move(1),
      PageDown: () => move(1),
      ArrowLeft: () => move(-1),
      PageUp: () => move(-1),
      Home: () => setPage(1),
      End: () => setPage(spread ? spreadPages(pageCount, pageCount)[0] : pageCount),
    };
    const action = handled[event.key];
    if (!action) return;
    event.preventDefault();
    action();
  };
  const download = async () => {
    if (state.status !== 'ready' || downloading) return;
    setDownloading(true);
    try {
      const data = await state.document.getData();
      const url = URL.createObjectURL(
        new Blob(
          [
            new Uint8Array(data),
          ],
          {
            type: 'application/pdf',
          },
        ),
      );
      const link = window.document.createElement('a');
      link.href = url;
      link.download = fileName;
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 0);
    } finally {
      setDownloading(false);
    }
  };
  const atStart = !pageCount || shown[0] <= 1;
  const atEnd = !pageCount || shown[shown.length - 1] >= pageCount;
  const pageWidth = Math.max(0, Math.floor((stageWidth - (shown.length - 1) * 12) / shown.length));
  const position =
    shown.length > 1
      ? `Pages ${shown[0]}–${shown[1]} of ${pageCount}`
      : `Page ${shown[0]} of ${pageCount}`;
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title}
      size="min(1100px, 100%)"
      fullScreen={phone}
      classNames={{
        body: classes.manualBody,
      }}
    >
      {/* Page keys work wherever focus is in the dialog, except in controls that use arrows themselves. */}
      <Stack
        gap="sm"
        onKeyDown={onKeyDown}
      >
        <Group
          justify="space-between"
          gap="xs"
          className={classes.manualToolbar}
        >
          <Group gap={4}>
            <Tooltip label="Previous page">
              <ActionIcon
                variant="subtle"
                aria-label="Previous page"
                // Stays focusable at the first page, so keyboard focus is never dropped from the dialog.
                data-disabled={atStart || undefined}
                aria-disabled={atStart}
                onClick={() => {
                  if (!atStart) move(-1);
                }}
              >
                <IconChevronLeft size={18} />
              </ActionIcon>
            </Tooltip>
            <Text
              size="sm"
              aria-live="polite"
              miw={120}
              ta="center"
            >
              {pageCount ? position : ' '}
            </Text>
            <Tooltip label="Next page">
              <ActionIcon
                variant="subtle"
                aria-label="Next page"
                data-disabled={atEnd || undefined}
                aria-disabled={atEnd}
                onClick={() => {
                  if (!atEnd) move(1);
                }}
              >
                <IconChevronRight size={18} />
              </ActionIcon>
            </Tooltip>
          </Group>
          <Group gap="xs">
            {!phone && (
              <SegmentedControl
                aria-label="Page layout"
                size="xs"
                value={layout}
                onChange={(value) => setLayout(value === 'spread' ? 'spread' : 'single')}
                data={[
                  {
                    value: 'single',
                    label: 'Single page',
                  },
                  {
                    value: 'spread',
                    label: 'Spreads',
                  },
                ]}
              />
            )}
            <Button
              {...workspaceActionProps}
              variant="light"
              size="compact-sm"
              leftSection={<IconDownload size={16} />}
              loading={downloading}
              disabled={state.status !== 'ready'}
              onClick={() => void download()}
            >
              Download
            </Button>
          </Group>
        </Group>
        <section
          ref={stage}
          aria-label={`${title} pages`}
          aria-roledescription="document viewer"
          className={classes.manualStage}
        >
          {state.status === 'loading' && <Loader aria-label="Loading manual" />}
          {state.status === 'failed' && (
            <Alert
              color="red"
              w="100%"
            >
              The manual could not be opened.{' '}
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={retry}
              >
                Retry
              </Button>
            </Alert>
          )}
          {state.status === 'ready' && (
            <Group
              gap={12}
              justify="center"
              wrap="nowrap"
              align="flex-start"
            >
              {shown.map((number) => (
                <Box key={number}>
                  <ManualPageCanvas
                    document={state.document}
                    page={number}
                    width={pageWidth}
                    label={`Page ${number}`}
                  />
                </Box>
              ))}
            </Group>
          )}
        </section>
        <Text
          size="xs"
          c="dimmed"
        >
          Use the arrow keys, Page Up and Page Down to turn pages.
        </Text>
      </Stack>
    </Modal>
  );
}
