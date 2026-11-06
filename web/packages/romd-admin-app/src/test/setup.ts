import '@testing-library/jest-dom/vitest';
import { client } from '@romd/admin-api-client';
import { cleanup } from '@testing-library/react';
import { afterAll, afterEach, beforeAll, vi } from 'vitest';
import { resetJobHandlers } from './msw/handlers/jobs';
import { server } from './msw/server';

// Configure API client for tests (must use the same client instance the SDK uses)
client.setConfig({
  baseUrl: '',
});

// Mantine 9 autosizing textareas subscribe to font loading; jsdom has no FontFaceSet.
if (!document.fonts) {
  Object.defineProperty(document, 'fonts', {
    configurable: true,
    value: new EventTarget(),
  });
}

const localStorageMock = (() => {
  const store = new Map<string, string>();

  return {
    get length() {
      return store.size;
    },
    clear: vi.fn(() => store.clear()),
    getItem: vi.fn((key: string) => store.get(key) ?? null),
    key: vi.fn((index: number) => Array.from(store.keys())[index] ?? null),
    removeItem: vi.fn((key: string) => store.delete(key)),
    setItem: vi.fn((key: string, value: string) => {
      store.set(key, value);
    }),
  };
})();

Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: localStorageMock,
});

Object.defineProperty(window, 'localStorage', {
  configurable: true,
  value: localStorageMock,
});

// Mock window.matchMedia for Mantine components
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation((query: string) => ({
    // An empty media query matches all media, as it does in browsers.
    matches: query === '',
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// Mock ResizeObserver for Mantine components
class ResizeObserverMock {
  observe = vi.fn();
  unobserve = vi.fn();
  disconnect = vi.fn();
}
global.ResizeObserver = ResizeObserverMock;

// jsdom has no layout; stub scrollIntoView for Mantine Combobox keyboard nav
Element.prototype.scrollIntoView = vi.fn();

// MSW server lifecycle
beforeAll(() =>
  server.listen({
    onUnhandledRequest: 'error',
  }),
);

afterEach(() => {
  cleanup();
  localStorage.clear();
  server.resetHandlers();
  resetJobHandlers();
});

afterAll(() => server.close());
