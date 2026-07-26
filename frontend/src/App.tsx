import { MsalProvider, AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom';
import { msalInstance, loginRequest } from './services/authConfig';
import { UploadPage } from './pages/Upload';
import { FileListPage } from './pages/FileList';
import { FileDetailPage } from './pages/FileDetail';
import { PreferencesPage } from './pages/Preferences';

/**
 * The application shell (T027).
 *
 * Nothing renders without an authenticated session. There is no anonymous route, no public
 * landing page, and no "preview without signing in" path — Principle I means every path to
 * content or metadata is authenticated, and the simplest way to guarantee that in a SPA is to
 * have no unauthenticated surface at all.
 */
export function App() {
  return (
    <MsalProvider instance={msalInstance}>
      <BrowserRouter>
        <AuthenticatedTemplate>
          <AuthenticatedShell />
        </AuthenticatedTemplate>
        <UnauthenticatedTemplate>
          <SignInPrompt />
        </UnauthenticatedTemplate>
      </BrowserRouter>
    </MsalProvider>
  );
}

function AuthenticatedShell() {
  const { instance } = useMsal();
  const account = instance.getActiveAccount();

  return (
    <div className="app-shell">
      {/*
        A skip link is the first focusable element on the page. It matters more here than in most
        applications because the preview is an iframe, and an iframe is a focus trap by default —
        without a way past it, a keyboard user reading a long document cannot reach the comment
        list at all (FR-081).
      */}
      <a className="skip-link" href="#main-content">
        Skip to main content
      </a>

      <header className="app-header">
        <nav aria-label="Primary">
          <Link to="/files" className="app-brand">
            BlinkMark
          </Link>
          <ul>
            <li>
              <Link to="/files">My files</Link>
            </li>
            <li>
              <Link to="/upload">Upload</Link>
            </li>
            <li>
              <Link to="/preferences">Preferences</Link>
            </li>
          </ul>
        </nav>

        <div className="app-account">
          <span>{account?.name ?? account?.username}</span>
          <button type="button" onClick={() => void instance.logoutRedirect()}>
            Sign out
          </button>
        </div>
      </header>

      <main id="main-content" tabIndex={-1}>
        <Routes>
          <Route path="/" element={<Navigate to="/files" replace />} />
          <Route path="/files" element={<FileListPage />} />
          <Route path="/files/:fileId" element={<FileDetailPage />} />
          <Route path="/upload" element={<UploadPage />} />
          <Route path="/preferences" element={<PreferencesPage />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
      </main>
    </div>
  );
}

function SignInPrompt() {
  const { instance } = useMsal();

  return (
    <main className="sign-in" id="main-content">
      <h1>BlinkMark</h1>
      <p>Share a draft, collect comments on the passages that matter, and let it delete itself.</p>
      <p className="scope-notice">
        Everything in BlinkMark is visible to members of your organization who have the link.
      </p>
      <button type="button" onClick={() => void instance.loginRedirect(loginRequest)}>
        Sign in with your work account
      </button>
    </main>
  );
}

function NotFound() {
  return (
    <section>
      <h1>Not found</h1>
      <p>
        That link does not point at anything. If it was a BlinkMark file, it may have expired — files delete
        themselves, and an expired file is indistinguishable from one that never existed.
      </p>
      <Link to="/files">Back to my files</Link>
    </section>
  );
}
