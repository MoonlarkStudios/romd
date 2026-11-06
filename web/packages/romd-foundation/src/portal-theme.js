// Runs in the document head in both the portals and hosted authentication.
// Use the same preference key and defaults as Mantine, without writing storage.
(() => {
  const root = document.documentElement;
  let preference = 'dark';
  if (root.dataset.romdPortal === 'consumer') {
    preference = 'dark';
  } else {
    try {
      preference = window.localStorage.getItem('mantine-color-scheme-value') || 'dark';
    } catch {
      // Storage may be disabled; the dark default still works.
    }
  }
  if (
    ![
      'light',
      'dark',
      'auto',
    ].includes(preference)
  )
    preference = 'dark';
  const dark =
    preference === 'dark' ||
    (preference !== 'light' && window.matchMedia('(prefers-color-scheme: dark)').matches);
  root.setAttribute('data-mantine-color-scheme', dark ? 'dark' : 'light');
})();
