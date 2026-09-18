import '@mantine/core/styles.css';
import '@mantine/carousel/styles.css';
import '@mantine/notifications/styles.css';
import '@romd/foundation/tokens.css';
import '@romd/foundation/typography.css';
import './styles/app.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './api/client';

document.body.classList.remove('romd-grain');
document.body.classList.add('romd-consumer');

const root = document.getElementById('app');

if (!root) {
  throw new Error('Root element not found');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
