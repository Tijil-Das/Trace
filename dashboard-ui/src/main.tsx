import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { App } from './App';
import { RecallProvider } from './state/RecallContext';

import './styles.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('index.html is missing #root — the bundle cannot mount.');
}

createRoot(container).render(
  <StrictMode>
    <RecallProvider>
      <App />
    </RecallProvider>
  </StrictMode>,
);
