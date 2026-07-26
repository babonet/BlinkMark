import { useEffect, useState } from 'react';
import { apiClient, type UserPreferences } from '../services/apiClient';

/** Notification preferences (T086, FR-040). */
export function PreferencesPage() {
  const [preferences, setPreferences] = useState<UserPreferences | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    apiClient
      .getPreferences()
      .then(setPreferences)
      .catch(() => setPreferences({ notificationsEnabled: true }));
  }, []);

  async function handleToggle(notificationsEnabled: boolean) {
    const updated = await apiClient.updatePreferences({ notificationsEnabled });
    setPreferences(updated);
    setStatus(notificationsEnabled ? 'Notifications are on.' : 'Notifications are off.');
  }

  if (!preferences) {
    return (
      <section>
        <h1>Preferences</h1>
        <p aria-live="polite">Loading…</p>
      </section>
    );
  }

  return (
    <section>
      <h1>Preferences</h1>

      <div>
        <input
          id="notifications-enabled"
          type="checkbox"
          checked={preferences.notificationsEnabled}
          onChange={(event) => void handleToggle(event.target.checked)}
        />
        <label htmlFor="notifications-enabled">Email me when someone comments on my files</label>
      </div>

      {/* Polite rather than assertive: a preference change is worth confirming and not worth
          interrupting whatever the user is doing next. */}
      <p aria-live="polite">{status}</p>
    </section>
  );
}
