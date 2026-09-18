import { PlayerBridge } from './bridge';
import { fetchPlayerRuntimeConfig, type PlayerRuntimeConfig } from './config';

interface PlayerBridgeController {
  start(): void;
  replayWindowMessage(event: MessageEvent): void;
}

export interface PlayerBootstrapDependencies {
  window: Pick<Window, 'addEventListener' | 'removeEventListener'>;
  loadConfig: () => Promise<PlayerRuntimeConfig>;
  createBridge: (config: PlayerRuntimeConfig) => PlayerBridgeController;
}

const defaultDependencies: PlayerBootstrapDependencies = {
  window,
  loadConfig: fetchPlayerRuntimeConfig,
  createBridge: (config) => new PlayerBridge(config),
};

export async function bootstrapPlayer(
  dependencies: PlayerBootstrapDependencies = defaultDependencies,
): Promise<PlayerBridgeController> {
  // The parent posts `init` when the iframe fires `load`, and the load event
  // does not wait for this module's asynchronous config fetch. Capture window
  // messages from the first synchronous instant and replay them once the
  // bridge is listening so the handshake cannot race the configuration load.
  // Duplicate deliveries are ignored by the bridge's single-init guard.
  const queuedMessages: MessageEvent[] = [];
  const queueMessage = (event: MessageEvent) => {
    queuedMessages.push(event);
  };
  dependencies.window.addEventListener('message', queueMessage);
  try {
    const config = await dependencies.loadConfig();
    const bridge = dependencies.createBridge(config);
    bridge.start();
    dependencies.window.removeEventListener('message', queueMessage);
    for (const event of queuedMessages) {
      bridge.replayWindowMessage(event);
    }
    return bridge;
  } catch (error) {
    dependencies.window.removeEventListener('message', queueMessage);
    throw error;
  }
}
