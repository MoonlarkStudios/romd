/** Presentation/accessibility adapter for EmulatorJS 4.2.3's existing panel.
 * Preserve its handlers, including mousedown-based key capture. */
export function enhanceControlPanel(panel: HTMLElement, trigger: HTMLElement): () => void {
  panel.classList.add('romd-controls');
  panel.setAttribute('role', 'region');
  panel.setAttribute('aria-label', 'Control Settings');
  const hint = panel.ownerDocument.createElement('p');
  hint.className = 'romd-controls-hint';
  hint.textContent = 'Choose a player, then select Set and press a key or controller button to change a mapping.';
  panel.querySelector('h4')?.after(hint);
  const links = panel.querySelectorAll<HTMLAnchorElement>('a');
  for (const link of links) {
    link.tabIndex = 0;
    if (!link.hasAttribute('role')) link.setAttribute('role', 'button');
  }
  const syncTabs = () => {
    for (const tab of panel.querySelectorAll<HTMLElement>('[role="tab"]')) {
      tab.setAttribute('aria-selected', String(tab.parentElement?.classList.contains('ejs_control_selected') === true));
    }
  };
  panel.querySelector('.ejs_control_player_bar')?.setAttribute('role', 'tablist');
  syncTabs();
  const keydown = (event: KeyboardEvent) => {
    // Once native capture is open, keys belong to the mapping engine.
    if (panel.querySelector('.ejs_popup_container:not([hidden])')) return;
    const target = event.target;
    if (!(target instanceof HTMLAnchorElement) || !panel.contains(target)) return;
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    event.stopPropagation();
    if (event.type === 'keydown') return;
    if (target.classList.contains('ejs_control_set_button')) {
      target.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true }));
    } else target.click();
  };
  const click = () => {
    syncTabs();
    if (panel.style.display === 'none') trigger.focus();
  };
  panel.addEventListener('keydown', keydown);
  panel.addEventListener('keyup', keydown);
  panel.addEventListener('click', click);
  return () => {
    panel.removeEventListener('keydown', keydown);
    panel.removeEventListener('keyup', keydown);
    panel.removeEventListener('click', click);
    hint.remove();
  };
}
