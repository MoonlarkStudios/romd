import '@romd/foundation/variables.css';
import '@romd/foundation/typography.css';
import './player.css';
import { bootstrapPlayer } from './bootstrap';

const status = document.querySelector<HTMLElement>('#status');
try {
  // bootstrapPlayer queues window messages from its first synchronous
  // instruction and replays them once the bridge listens, so the parent's
  // `init` (posted on iframe load) cannot race the async config fetch.
  await bootstrapPlayer();
  if (status) {
    status.textContent = 'Waiting for ROMD…';
  }
} catch (error) {
  if (status) {
    status.textContent = error instanceof Error ? error.message : 'Player configuration failed.';
  }
}
