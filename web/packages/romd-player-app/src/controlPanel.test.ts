import { describe, expect, it, vi } from 'vitest';
import { enhanceControlPanel } from './controlPanel';

it('activates native remapping after releasing Enter without leaking it into capture', () => {
  const panel = document.createElement('div');
  panel.innerHTML = '<h4>Control Settings</h4><div class="ejs_control_bar"><a class="ejs_control_set_button">Set</a></div><div class="ejs_popup_container" hidden></div>';
  const nativeCapture = vi.fn();
  const row = panel.querySelector('.ejs_control_bar');
  row?.addEventListener('mousedown', () => { nativeCapture(); panel.querySelector('[hidden]')?.removeAttribute('hidden'); });
  const clean = enhanceControlPanel(panel, document.createElement('button'));
  const set = panel.querySelector('a');
  expect(set?.tabIndex).toBe(0);
  set?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
  expect(nativeCapture).not.toHaveBeenCalled();
  set?.dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', bubbles: true }));
  expect(nativeCapture).toHaveBeenCalledOnce();
  set?.dispatchEvent(new KeyboardEvent('keyup', { key: ' ', bubbles: true }));
  expect(nativeCapture).toHaveBeenCalledOnce();
  clean();
});

describe('native control panel enhancement', () => {
  it('keeps native player selection and close handlers and removes its listeners', () => {
    const panel = document.createElement('div');
    panel.innerHTML = '<h4>Control Settings</h4><ul class="ejs_control_player_bar"><li class="ejs_control_selected"><a role="tab">Player 1</a></li><li><a role="tab">Player 2</a></li></ul><a class="ejs_button">Close</a>';
    const trigger = document.createElement('button');
    document.body.append(panel, trigger);
    const tabs = panel.querySelectorAll('a[role="tab"]');
    tabs[1].addEventListener('click', () => { tabs[0].parentElement?.classList.remove('ejs_control_selected'); tabs[1].parentElement?.classList.add('ejs_control_selected'); });
    const close = panel.querySelector<HTMLElement>('.ejs_button');
    close?.addEventListener('click', () => { panel.style.display = 'none'; });
    const clean = enhanceControlPanel(panel, trigger);
    tabs[1].dispatchEvent(new KeyboardEvent('keyup', { key: 'Enter', bubbles: true }));
    expect(tabs[1].getAttribute('aria-selected')).toBe('true');
    close?.dispatchEvent(new KeyboardEvent('keyup', { key: ' ', bubbles: true }));
    expect(document.activeElement).toBe(trigger);
    clean();
    panel.style.display = '';
    close?.dispatchEvent(new KeyboardEvent('keyup', { key: ' ', bubbles: true }));
    expect(panel.style.display).toBe('');
    panel.remove(); trigger.remove();
  });
});
