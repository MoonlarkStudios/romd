import { copyOlderSaves, inspectOlderSaves } from './saveStorage';

/** Before core startup, offer explicit recovery of the old, unowned shared data. */
export async function prepareBrowserSaves(
  window: Window,
  document: Document,
  contentSha256: string,
  saveId: string,
  signal: AbortSignal,
  onRecovery: () => void,
  forceRecovery: boolean,
): Promise<void> {
  if (!window.indexedDB)
    throw new Error('Browser save storage is unavailable. Allow site storage and retry.');
  const recovery = await inspectOlderSaves(window.indexedDB, contentSha256, saveId, !forceRecovery);
  if (signal.aborted || recovery.files.length === 0) return;
  if (recovery.hasCurrentSaves && !forceRecovery) return;
  onRecovery();

  await new Promise<void>((resolve) => {
    const panel = document.createElement('section');
    panel.className = 'romd-save-recovery';
    panel.setAttribute('aria-labelledby', 'save-recovery-heading');
    const heading = document.createElement('h2');
    heading.id = 'save-recovery-heading';
    heading.textContent = 'Older shared saves found';
    const explanation = document.createElement('p');
    explanation.textContent =
      'These saves were created before accounts had separate progress. Copy them only if they are yours. The originals will be kept.';
    const status = document.createElement('p');
    status.setAttribute('role', 'status');
    if (recovery.hasCurrentSaves)
      status.textContent =
        'You already have progress for this game. Download older files to import them manually; your progress will not be replaced.';
    const downloads = document.createElement('ul');
    const urls: string[] = [];
    for (const file of recovery.files) {
      const item = document.createElement('li');
      const link = document.createElement('a');
      const url = URL.createObjectURL(
        new Blob([
          file.data,
        ]),
      );
      urls.push(url);
      link.href = url;
      link.download = file.name;
      link.textContent =
        file.store === 'states'
          ? 'Download older save state'
          : `Download older in-game save (${file.name.split('.').pop()})`;
      item.append(link);
      downloads.append(item);
    }
    const copy = document.createElement('button');
    copy.type = 'button';
    copy.textContent = 'Copy older saves into my account';
    copy.disabled = recovery.hasCurrentSaves;
    const proceed = document.createElement('button');
    proceed.type = 'button';
    proceed.textContent = recovery.hasCurrentSaves
      ? 'Continue with my saves'
      : 'Start with separate saves';
    const finish = () => {
      panel.remove();
      for (const url of urls) URL.revokeObjectURL(url);
      signal.removeEventListener('abort', finish);
      resolve();
    };
    proceed.addEventListener('click', finish);
    copy.addEventListener('click', async () => {
      copy.disabled = true;
      proceed.disabled = true;
      try {
        await copyOlderSaves(window.indexedDB, recovery);
        finish();
      } catch {
        status.textContent =
          'Copying could not finish. Some files may have been copied. Originals are preserved and existing progress was not replaced. Download the older files to recover them manually.';
        proceed.textContent = 'Continue with available saves';
        proceed.disabled = false;
      }
    });
    panel.append(heading, explanation, status, downloads, copy, proceed);
    document.body.append(panel);
    signal.addEventListener('abort', finish, {
      once: true,
    });
    if (signal.aborted) finish();
    else proceed.focus();
  });
}
