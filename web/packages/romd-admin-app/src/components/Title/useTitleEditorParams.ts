import { useLocation, useSearchParams } from 'react-router';
import { type ArtworkRole, artworkRoles } from '../artworkPresentation';

export const titleEditors = [
  'match',
  'metadata',
  'artwork',
] as const;
export type TitleEditor = (typeof titleEditors)[number];

const editorParamKeys = [
  'editor',
  'requirement',
  'role',
] as const;
const legacyWorkspaceEditors: Record<string, TitleEditor> = {
  providers: 'match',
  metadata: 'metadata',
  artwork: 'artwork',
};

/** Copies search params without editor state, for navigation that changes the review context. */
export function withoutTitleEditorParams(params: URLSearchParams): URLSearchParams {
  const next = new URLSearchParams(params);
  for (const key of editorParamKeys) next.delete(key);
  return next;
}

/**
 * Rewrites enrichment deep links from before the shared workbench (`tab`, `issue`) to editor
 * params, preserving every other param. Returns undefined when nothing needs rewriting. Only the
 * enrichment route may apply this: title detail keeps `tab` for its page sections.
 */
export function normalizeLegacyWorkspaceEditorParams(
  params: URLSearchParams,
): URLSearchParams | undefined {
  if (!params.has('tab') && !params.has('issue')) return undefined;
  const next = new URLSearchParams(params);
  const editor = legacyWorkspaceEditors[params.get('tab') ?? ''];
  const requirement = params.get('issue');
  next.delete('tab');
  next.delete('issue');
  if (editor && !next.has('editor')) next.set('editor', editor);
  if (requirement && !next.has('requirement')) next.set('requirement', requirement);
  return next;
}

const titleDetailSections = [
  'match',
  'metadata',
  'artwork',
  'releases',
  'ratings',
  'enrichment',
];

/**
 * Rewrites title page links from before the shared workbench (`tab`) to the `editor` param, preserving every
 * other param. Returns undefined when nothing needs rewriting. Only the title page may apply this.
 */
export function normalizeLegacyTitleDetailParams(
  params: URLSearchParams,
): URLSearchParams | undefined {
  if (!params.has('tab')) return undefined;
  const next = new URLSearchParams(params);
  const section = params.get('tab') ?? '';
  next.delete('tab');
  if (titleDetailSections.includes(section) && !next.has('editor')) next.set('editor', section);
  return next;
}

/** The single owner of title editor URL state on both workbench routes. */
export function useTitleEditorParams() {
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const requestedEditor = params.get('editor');
  const requestedRole = params.get('role');
  const editor = titleEditors.find((value) => value === requestedEditor);
  const requirement = params.get('requirement') ?? undefined;
  const role = artworkRoles.find((value) => value === requestedRole);

  /** Switching editors keeps the linked requirement and role; a context replaces both. */
  const openEditor = (
    next: TitleEditor,
    context?: {
      requirement?: string;
      role?: ArtworkRole;
    },
  ) => {
    const updated = new URLSearchParams(params);
    updated.set('editor', next);
    if (context) {
      if (context.requirement) updated.set('requirement', context.requirement);
      else updated.delete('requirement');
      if (context.role) updated.set('role', context.role);
      else updated.delete('role');
    }
    setParams(updated, {
      state: location.state,
    });
  };
  const closeEditor = () =>
    setParams(withoutTitleEditorParams(params), {
      state: location.state,
    });

  return {
    editor,
    requirement,
    role,
    openEditor,
    closeEditor,
  };
}
