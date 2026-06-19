// Tiny localStorage helpers so a browser refresh can re-attach to the grain
// that already lives in the Orleans cluster, instead of forcing the user to
// re-join. We only persist the grain key (plus a little context to render the
// UI before the first poll returns); all authoritative state still comes from
// the cluster.
import { browser } from '$app/environment';

export interface AttendeeSession {
  key: string;
  name: string;
}

export interface PresenterSession {
  key: string;
  name: string;
  // The presenter password is intentionally NOT persisted. It never touches
  // localStorage (or the shipped bundle) — the presenter re-enters it to
  // reconnect after a reload. Only the grain key and name are stored so the
  // session can be re-attached to the existing grain in the cluster.
}

const ATTENDEE_KEY = 'orleans-demo:attendee';
const PRESENTER_KEY = 'orleans-demo:presenter';

function read<T>(storageKey: string): T | null {
  if (!browser) return null;
  try {
    const raw = localStorage.getItem(storageKey);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write(storageKey: string, value: unknown): void {
  if (!browser) return;
  try {
    localStorage.setItem(storageKey, JSON.stringify(value));
  } catch {
    /* storage unavailable (private mode / quota) — degrade gracefully */
  }
}

function clear(storageKey: string): void {
  if (!browser) return;
  try {
    localStorage.removeItem(storageKey);
  } catch {
    /* ignore */
  }
}

export const attendeeSession = {
  load: () => read<AttendeeSession>(ATTENDEE_KEY),
  save: (session: AttendeeSession) => write(ATTENDEE_KEY, session),
  clear: () => clear(ATTENDEE_KEY)
};

export const presenterSession = {
  load: () => read<PresenterSession>(PRESENTER_KEY),
  save: (session: PresenterSession) => write(PRESENTER_KEY, session),
  clear: () => clear(PRESENTER_KEY)
};
