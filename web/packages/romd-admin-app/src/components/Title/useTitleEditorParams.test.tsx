import { act, renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it } from 'vitest';
import {
  normalizeLegacyTitleDetailParams,
  normalizeLegacyWorkspaceEditorParams,
  useTitleEditorParams,
  withoutTitleEditorParams,
} from './useTitleEditorParams';

function renderEditorParams(entry: string) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <MemoryRouter
      initialEntries={[
        entry,
      ]}
    >
      {children}
    </MemoryRouter>
  );
  return renderHook(
    () => ({
      ...useTitleEditorParams(),
      search: useLocation().search,
    }),
    {
      wrapper,
    },
  );
}

describe('useTitleEditorParams', () => {
  it('round-trips an editor with its requirement and role without touching the page tab', () => {
    const { result } = renderEditorParams('/titles/t1?tab=releases&view=All');
    expect(result.current).toMatchObject({
      editor: undefined,
      requirement: undefined,
      role: undefined,
    });
    act(() =>
      result.current.openEditor('artwork', {
        requirement: 'Artwork:Poster',
        role: 'Poster',
      }),
    );
    expect(result.current).toMatchObject({
      editor: 'artwork',
      requirement: 'Artwork:Poster',
      role: 'Poster',
    });
    expect(result.current.search).toBe(
      '?tab=releases&view=All&editor=artwork&requirement=Artwork%3APoster&role=Poster',
    );
    act(() => result.current.closeEditor());
    expect(result.current).toMatchObject({
      editor: undefined,
      requirement: undefined,
      role: undefined,
    });
    expect(result.current.search).toBe('?tab=releases&view=All');
  });

  it('keeps the linked requirement and role when switching editors', () => {
    const { result } = renderEditorParams(
      '/enrichment?title=t1&editor=metadata&requirement=Metadata%3ADescription&role=Logo',
    );
    act(() => result.current.openEditor('artwork'));
    expect(result.current).toMatchObject({
      editor: 'artwork',
      requirement: 'Metadata:Description',
      role: 'Logo',
    });
  });

  it('replaces the linked requirement and role when opening an editor for a new requirement', () => {
    const { result } = renderEditorParams(
      '/enrichment?title=t1&editor=artwork&requirement=Artwork%3ALogo&role=Logo',
    );
    act(() =>
      result.current.openEditor('match', {
        requirement: 'Identity:igdb',
      }),
    );
    expect(result.current).toMatchObject({
      editor: 'match',
      requirement: 'Identity:igdb',
      role: undefined,
    });
    expect(result.current.search).toBe('?title=t1&editor=match&requirement=Identity%3Aigdb');
  });

  it('ignores unknown editors and roles, and never reads the page tab as an editor', () => {
    const { result } = renderEditorParams('/titles/t1?tab=artwork&editor=providers&role=Banner');
    expect(result.current).toMatchObject({
      editor: undefined,
      role: undefined,
    });
    expect(result.current.search).toBe('?tab=artwork&editor=providers&role=Banner');
  });
});

describe('withoutTitleEditorParams', () => {
  it('drops only editor state', () => {
    const params = new URLSearchParams(
      'view=Blocked&q=zelda&title=t1&editor=artwork&requirement=Artwork%3ALogo&role=Logo&activity=a1',
    );
    expect(withoutTitleEditorParams(params).toString()).toBe(
      'view=Blocked&q=zelda&title=t1&activity=a1',
    );
    expect(params.get('editor')).toBe('artwork');
  });
});

describe('normalizeLegacyWorkspaceEditorParams', () => {
  it.each([
    [
      'providers',
      'match',
    ],
    [
      'metadata',
      'metadata',
    ],
    [
      'artwork',
      'artwork',
    ],
  ])('maps the legacy %s tab to the %s editor and preserves every other param', (tab, editor) => {
    const legacy = new URLSearchParams(
      `view=Automatic&q=zelda&title=t1&tab=${tab}&issue=Artwork%3ABackdrop&role=Backdrop&activity=a1`,
    );
    const next = normalizeLegacyWorkspaceEditorParams(legacy);
    expect(Object.fromEntries(next ?? [])).toEqual({
      view: 'Automatic',
      q: 'zelda',
      title: 't1',
      role: 'Backdrop',
      activity: 'a1',
      editor,
      requirement: 'Artwork:Backdrop',
    });
  });

  it('maps a lone legacy issue to a requirement and drops an unknown legacy tab', () => {
    expect(
      normalizeLegacyWorkspaceEditorParams(
        new URLSearchParams('title=t1&issue=Identity%3Aigdb'),
      )?.toString(),
    ).toBe('title=t1&requirement=Identity%3Aigdb');
    expect(
      normalizeLegacyWorkspaceEditorParams(
        new URLSearchParams('title=t1&tab=releases'),
      )?.toString(),
    ).toBe('title=t1');
  });

  it('keeps current editor params over legacy ones', () => {
    const next = normalizeLegacyWorkspaceEditorParams(
      new URLSearchParams(
        'editor=artwork&requirement=Artwork%3ALogo&tab=providers&issue=Identity%3Aigdb',
      ),
    );
    expect(next?.toString()).toBe('editor=artwork&requirement=Artwork%3ALogo');
  });

  it('leaves current links untouched', () => {
    expect(
      normalizeLegacyWorkspaceEditorParams(new URLSearchParams('view=All&title=t1&editor=match')),
    ).toBeUndefined();
  });
});

describe('normalizeLegacyTitleDetailParams', () => {
  it.each([
    'match',
    'metadata',
    'artwork',
    'releases',
    'ratings',
  ])('moves ?tab=%s to the editor param, preserving other params', (section) => {
    expect(
      normalizeLegacyTitleDetailParams(
        new URLSearchParams(`tab=${section}&release=g1`),
      )?.toString(),
    ).toBe(`release=g1&editor=${section}`);
  });

  it('drops unknown tabs, keeps an explicit editor, and leaves current links alone', () => {
    expect(
      normalizeLegacyTitleDetailParams(new URLSearchParams('tab=curation&release=g1'))?.toString(),
    ).toBe('release=g1');
    expect(
      normalizeLegacyTitleDetailParams(
        new URLSearchParams('tab=releases&editor=artwork'),
      )?.toString(),
    ).toBe('editor=artwork');
    expect(normalizeLegacyTitleDetailParams(new URLSearchParams('editor=ratings'))).toBeUndefined();
  });
});
