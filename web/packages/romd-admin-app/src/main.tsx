import { createClient } from '@romd/admin-api-client';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './api/client'; // Configures interceptors

createClient({
  baseUrl: '',
});

const root = document.getElementById('app');

if (!root) {
  throw new Error('Root element not found');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
