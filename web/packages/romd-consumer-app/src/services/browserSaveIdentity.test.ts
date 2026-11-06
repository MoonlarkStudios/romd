import { createBrowserSaveId } from './browserSaveIdentity';

const content = 'a'.repeat(64);

describe('browser save ownership', () => {
  it('stays stable across sign-in sessions', async () => {
    const first = await createBrowserSaveId('https://romd.example', 'person-a', content);
    expect(first).toMatch(/^[a-f0-9]{64}$/);
    expect(await createBrowserSaveId('https://romd.example', 'person-a', content)).toBe(first);
    expect(first).not.toBe(content);
  });

  it('separates accounts, installations and different ROM revisions', async () => {
    const identities = await Promise.all([
      createBrowserSaveId('https://romd.example', 'person-a', content),
      createBrowserSaveId('https://romd.example', 'person-b', content),
      createBrowserSaveId('https://other.example', 'person-a', content),
      createBrowserSaveId('https://romd.example', 'person-a', 'b'.repeat(64)),
    ]);
    expect(new Set(identities).size).toBe(4);
  });

  it('has no anonymous fallback or unverified-content identity', async () => {
    await expect(createBrowserSaveId('https://romd.example', '', content)).rejects.toThrow(
      /signed-in account/,
    );
    await expect(
      createBrowserSaveId('https://romd.example', 'person-a', 'invalid'),
    ).rejects.toThrow(/verified game/);
  });
});
