/** Stable local save identity. Account names, tokens and credentials never enter the player. */
export async function createBrowserSaveId(
  serverInstanceId: string,
  userId: string,
  contentSha256: string,
): Promise<string> {
  if (!serverInstanceId.trim() || !userId.trim() || !/^[a-f0-9]{64}$/.test(contentSha256)) {
    throw new Error('A signed-in account and verified game are required for browser saves.');
  }
  const input = new TextEncoder().encode(
    JSON.stringify([
      'romd.browser-saves.v2',
      serverInstanceId,
      userId,
      contentSha256,
    ]),
  );
  const digest = await crypto.subtle.digest('SHA-256', input);
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
}
