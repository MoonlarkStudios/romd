import { authHandlers } from './auth';
import { datHandlers } from './dats';
import { diagnosticsHandlers } from './diagnostics';
import { jobHandlers } from './jobs';
import { platformHandlers } from './platforms';
import { romHandlers } from './roms';
import { uploadHandlers } from './upload';

export const handlers = [
  ...romHandlers,
  ...datHandlers,
  ...diagnosticsHandlers,
  ...platformHandlers,
  ...authHandlers,
  ...jobHandlers,
  ...uploadHandlers,
];
