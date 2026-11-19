import { describe, expect, it } from 'vitest';
import { portalTheme } from '../../../config/portalTheme';

describe('Admin first-paint palette', () => {
  it('layers the Admin palette over the shared stylesheet only in the Admin SPA', () => {
    const shared = portalTheme()
      .transformIndexHtml.handler()
      .find(({ tag }) => tag === 'style');
    const admin = portalTheme('admin')
      .transformIndexHtml.handler()
      .find(({ tag }) => tag === 'style');

    expect(admin?.injectTo).toBe('head-prepend');
    expect(shared?.children).toContain('--romd-portal-bg');
    expect(admin?.children.startsWith(shared?.children ?? '')).toBe(true);
    expect(admin?.children).toContain(":root[data-romd-portal='admin']");
    expect(admin?.children).toContain('--romd-admin-field');
    expect(shared?.children).not.toContain('--romd-admin-field');
  });
});
