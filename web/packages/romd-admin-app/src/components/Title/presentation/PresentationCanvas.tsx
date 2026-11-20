import { Badge, SegmentedControl, Text, UnstyledButton } from '@mantine/core';
import type { ResolvedArtwork } from '@romd/admin-api-client';
import {
  artworkCrop,
  artworkObjectStyle,
  DeviceFrame,
  type PreviewDevice,
  previewDevices,
} from '@romd/consumer-ui';
import {
  type CSSProperties,
  type ReactNode,
  type RefObject,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { type ArtworkRole, artworkRoleLabel, artworkRoles } from '../../artworkPresentation';
import classes from '../ArtworkWorkspace.module.css';
import type { CropFrame } from '../FocalPointEditor';
import { type ArtworkPreviewTitle, SurfacePreview } from '../preview/ArtworkRolePreview';
import { PreviewDeviceSwitcher } from '../preview/PreviewDeviceSwitcher';
import {
  cropProbes,
  isPresentationSurface,
  type PresentationSurface,
  presentationSurfaces,
  type RolePresentationState,
  roleStateColor,
  roleStateLabel,
  roleTargets,
} from './presentationModel';

interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}

const sameBoxes = (a: Partial<Record<ArtworkRole, Box>>, b: Partial<Record<ArtworkRole, Box>>) =>
  artworkRoles.every(
    (role) =>
      a[role]?.left === b[role]?.left &&
      a[role]?.top === b[role]?.top &&
      a[role]?.width === b[role]?.width &&
      a[role]?.height === b[role]?.height,
  );

/**
 * Measures where each role sits in the rendered (inert, scaled) consumer surface, relative to `stage`, so buttons
 * laid over it line up with what players see. Images loading and the stage resizing both move the elements.
 */
function useRoleBoxes(
  stage: RefObject<HTMLElement | null>,
  surface: PresentationSurface,
  layoutKey: string,
) {
  const [boxes, setBoxes] = useState<Partial<Record<ArtworkRole, Box>>>({});
  useLayoutEffect(() => {
    const root = stage.current;
    if (!root || !layoutKey) return;
    const measure = () => {
      const base = root.getBoundingClientRect();
      const next: Partial<Record<ArtworkRole, Box>> = {};
      for (const [role, selectors] of Object.entries(roleTargets[surface]) as [
        ArtworkRole,
        readonly string[],
      ][]) {
        const element = selectors
          .map((selector) => root.querySelector(selector))
          .find((item) => item !== null);
        if (!element) continue;
        const rect = element.getBoundingClientRect();
        // A fixed-height device clips the page; keep each area inside the visible screen.
        const left = Math.max(0, rect.left - base.left);
        const top = Math.max(0, rect.top - base.top);
        const width = Math.min(rect.right - base.left, base.width) - left;
        const height = Math.min(rect.bottom - base.top, base.height) - top;
        if (width > 0 && height > 0)
          next[role] = {
            left,
            top,
            width,
            height,
          };
      }
      setBoxes((previous) => (sameBoxes(previous, next) ? previous : next));
    };
    measure();
    const frame = requestAnimationFrame(measure);
    root.addEventListener('load', measure, true);
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    observer?.observe(root);
    const screen = root.querySelector('figure > div');
    if (screen) observer?.observe(screen);
    return () => {
      cancelAnimationFrame(frame);
      root.removeEventListener('load', measure, true);
      observer?.disconnect();
    };
  }, [
    stage,
    surface,
    layoutKey,
  ]);
  return boxes;
}

export interface RoleStatus {
  state: RolePresentationState;
  /** Set when the role has an unsaved change, naming it. */
  staged?: string;
  result?: 'Saved' | 'Needs attention';
}

/**
 * The presentation laid out as players see it: a consumer surface at a device's real width, scaled to fit, with each
 * role it shows outlined as a button. The separate role strip reaches every role, including those outside this view.
 */
export function PresentationCanvas({
  title,
  surface,
  onSurfaceChange,
  device,
  onDeviceChange,
  status,
  selectedRole,
  panelId,
  disabled,
  onSelect,
  comparison,
}: {
  title: ArtworkPreviewTitle;
  surface: PresentationSurface;
  onSurfaceChange: (surface: PresentationSurface) => void;
  device: PreviewDevice;
  onDeviceChange: (device: PreviewDevice) => void;
  status: (role: ArtworkRole) => RoleStatus;
  selectedRole?: ArtworkRole;
  panelId: string;
  disabled: boolean;
  onSelect: (role: ArtworkRole, trigger: HTMLElement) => void;
  /** Shown in the toolbar when there are changes to compare, such as a Proposed/Current switch. */
  comparison?: ReactNode;
}) {
  const stage = useRef<HTMLDivElement>(null);
  const layoutKey = [
    surface,
    device,
    ...(title.artwork ?? []).map((item) => `${item.role}:${item.url ?? ''}`),
  ].join('|');
  const boxes = useRoleBoxes(stage, surface, layoutKey);
  const roles = presentationSurfaces.find((item) => item.value === surface)?.roles ?? [];
  const { width } = previewDevices[device];
  // Larger areas first, so smaller ones inside them (a poster over the backdrop) stay on top.
  const ordered = roles
    .filter((role) => boxes[role])
    .sort(
      (a, b) =>
        (boxes[b]?.width ?? 0) * (boxes[b]?.height ?? 0) -
        (boxes[a]?.width ?? 0) * (boxes[a]?.height ?? 0),
    );
  return (
    <section
      aria-label="Presentation canvas"
      className={classes.canvas}
    >
      <div className={classes.canvasToolbar}>
        <SegmentedControl
          aria-label="Preview surface"
          size="xs"
          value={surface}
          onChange={(value) => {
            if (isPresentationSurface(value)) onSurfaceChange(value);
          }}
          data={presentationSurfaces.map((item) => ({
            value: item.value,
            label: item.label,
          }))}
        />
        {comparison}
        <PreviewDeviceSwitcher
          value={device}
          onChange={onDeviceChange}
        />
      </div>
      <div className={classes.canvasStage}>
        <div
          ref={stage}
          className={classes.canvasFrame}
          data-device={device}
          style={
            {
              '--frame-width': `${width}px`,
            } as CSSProperties
          }
        >
          <DeviceFrame device={device}>
            <div aria-hidden="true">
              <SurfacePreview
                title={title}
                surface={surface}
                device={device}
              />
            </div>
          </DeviceFrame>
          <div className={classes.hotspots}>
            {ordered.map((role) => {
              const box = boxes[role];
              const { state, staged, result } = status(role);
              const label = artworkRoleLabel(role);
              return (
                <UnstyledButton
                  key={role}
                  className={classes.hotspot}
                  aria-label={`Edit ${label.toLowerCase()} in preview`}
                  aria-pressed={selectedRole === role}
                  aria-controls={selectedRole === role ? panelId : undefined}
                  data-selected={selectedRole === role || undefined}
                  data-staged={staged ? true : undefined}
                  disabled={disabled}
                  onClick={(event) => onSelect(role, event.currentTarget)}
                  style={{
                    left: box?.left,
                    top: box?.top,
                    width: box?.width,
                    height: box?.height,
                  }}
                >
                  <span className={classes.hotspotLabel}>
                    {label}
                    <span className={classes.hotspotState}>
                      {result === 'Needs attention'
                        ? result
                        : staged
                          ? 'Unsaved'
                          : roleStateLabel(state)}
                    </span>
                  </span>
                </UnstyledButton>
              );
            })}
          </div>
        </div>
      </div>
    </section>
  );
}

/** Every role at a glance with its state, including roles the chosen surface does not show. */
export function RoleStrip({
  artwork,
  status,
  selectedRole,
  panelId,
  disabled,
  onSelect,
}: {
  artwork: readonly ResolvedArtwork[];
  status: (role: ArtworkRole) => RoleStatus;
  selectedRole?: ArtworkRole;
  panelId: string;
  disabled: boolean;
  onSelect: (role: ArtworkRole, trigger: HTMLElement) => void;
}) {
  const id = useId();
  return (
    <fieldset
      aria-label="Presentation roles"
      className={classes.roleStrip}
    >
      {artworkRoles.map((role) => {
        const image = artwork.find((item) => item.role === role);
        const { state, staged, result } = status(role);
        const label = artworkRoleLabel(role);
        const describedBy = `${id}-${role}`;
        return (
          <UnstyledButton
            key={role}
            className={classes.roleChip}
            aria-label={`Edit ${label.toLowerCase()}`}
            aria-describedby={describedBy}
            aria-expanded={selectedRole === role}
            aria-controls={selectedRole === role ? panelId : undefined}
            data-selected={selectedRole === role || undefined}
            disabled={disabled}
            onClick={(event) => onSelect(role, event.currentTarget)}
          >
            <span
              className={classes.roleChipThumb}
              data-role={role}
            >
              {image?.url && (
                <img
                  src={image.url}
                  alt=""
                  style={artworkObjectStyle(role, artworkCrop(image))}
                />
              )}
            </span>
            <span className={classes.roleChipText}>
              <Text
                component="span"
                size="sm"
                fw={600}
              >
                {label}
              </Text>
              <span id={describedBy}>
                <Badge
                  size="xs"
                  variant="light"
                  color={
                    result === 'Needs attention' ? 'red' : staged ? 'blue' : roleStateColor(state)
                  }
                  className={classes.roleChipBadge}
                >
                  {result === 'Needs attention'
                    ? result
                    : staged
                      ? `Draft · ${staged}`
                      : [
                          roleStateLabel(state),
                          result,
                        ]
                          .filter(Boolean)
                          .join(' · ')}
                </Badge>
              </span>
            </span>
          </UnstyledButton>
        );
      })}
    </fieldset>
  );
}

/**
 * Renders the consumer frames a role is cropped to, out of sight, and measures their proportions, so the focal point
 * editor can outline exactly what each device shows. Unmeasured frames (no layout, such as in tests) are left out.
 */
export function CropFrames({
  title,
  role,
  children,
}: {
  title: ArtworkPreviewTitle;
  role: ArtworkRole;
  children: (frames: CropFrame[]) => ReactNode;
}) {
  const probes = useMemo(
    () => cropProbes(role),
    [
      role,
    ],
  );
  const root = useRef<HTMLDivElement>(null);
  const [frames, setFrames] = useState<CropFrame[]>([]);
  const url = title.artwork?.find((item) => item.role === role)?.url ?? '';
  useLayoutEffect(() => {
    const container = root.current;
    if (!container || !url) return;
    const measure = () => {
      const next: CropFrame[] = [];
      for (const probe of probes) {
        const selector = roleTargets[probe.surface][role]?.[0];
        const element = selector
          ? container.querySelector(`[data-probe="${probe.label}"]`)?.querySelector(selector)
          : null;
        const rect = element?.getBoundingClientRect();
        if (rect && rect.width > 0 && rect.height > 0)
          next.push({
            label: probe.label,
            aspect: rect.width / rect.height,
          });
      }
      setFrames((previous) =>
        previous.length === next.length &&
        previous.every(
          (frame, index) =>
            frame.label === next[index]?.label &&
            Math.abs(frame.aspect - (next[index]?.aspect ?? 0)) < 0.001,
        )
          ? previous
          : next,
      );
    };
    measure();
    container.addEventListener('load', measure, true);
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    for (const probe of probes) {
      const selector = roleTargets[probe.surface][role]?.[0];
      const target =
        selector &&
        container.querySelector(`[data-probe="${probe.label}"]`)?.querySelector(selector);
      if (target) observer?.observe(target);
    }
    return () => {
      container.removeEventListener('load', measure, true);
      observer?.disconnect();
    };
  }, [
    url,
    role,
    probes,
  ]);
  return (
    <>
      <div
        ref={root}
        aria-hidden="true"
        className={classes.cropProbe}
      >
        {url &&
          probes.map((probe) => (
            <div
              key={probe.label}
              data-probe={probe.label}
            >
              <DeviceFrame
                device={probe.device}
                fixedHeight={probe.surface !== 'library'}
              >
                <SurfacePreview
                  title={title}
                  surface={probe.surface}
                  device={probe.device}
                />
              </DeviceFrame>
            </div>
          ))}
      </div>
      {children(frames)}
    </>
  );
}
