import '@testing-library/jest-dom/vitest';
import { vi } from 'vitest';

// Mantine providers read media preferences; jsdom has no matchMedia.
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
