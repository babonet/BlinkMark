import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { EventType, type AuthenticationResult } from '@azure/msal-browser';
import { msalInstance } from './services/authConfig';
import { App } from './App';
import './styles.css';

async function bootstrap(): Promise<void> {
  await msalInstance.initialize();

  const accounts = msalInstance.getAllAccounts();
  if (accounts.length > 0) {
    msalInstance.setActiveAccount(accounts[0]);
  }

  msalInstance.addEventCallback((event) => {
    if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
      msalInstance.setActiveAccount((event.payload as AuthenticationResult).account);
    }
  });

  await msalInstance.handleRedirectPromise();

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}

void bootstrap();
