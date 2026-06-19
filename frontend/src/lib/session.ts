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
  // Saved only after the presenter has entered it once, so a reload can
  // reconnect without re-prompting. It is never pre-filled into the bundle and
  // is cleared on sign out. This is a demo with a shared, non-secret password;
  // do not store real credentials in localStorage in production.
  password: string;
}

const ATTENDEE_KEY = 'orleans-demo:attendee';
const PRESENTER_KEY = 'orleans-demo:presenter';
const CLIENT_ID_KEY = 'orleans-demo:client-id';

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

// A stable per-browser id, sent as X-Session-Id on every /api call so the
// backend rate limiter can partition by client instead of by IP. Without this,
// many attendees behind one venue NAT would share a single IP partition and
// trip the limit. Generated lazily and persisted so it survives reloads.
let cachedClientId: string | null = null;

export function clientId(): string {
  if (cachedClientId) return cachedClientId;
  if (!browser) return 'ssr';

  let id: string | null = null;
  try {
    id = localStorage.getItem(CLIENT_ID_KEY);
  } catch {
    /* storage unavailable — fall through and use an ephemeral id */
  }

  if (!id) {
    id = crypto.randomUUID();
    try {
      localStorage.setItem(CLIENT_ID_KEY, id);
    } catch {
      /* ignore — id stays in-memory for this page load only */
    }
  }

  cachedClientId = id;
  return id;
}

// Header bag that tags a request with the per-browser session id. Spread into
// each fetch's headers, e.g. `headers: { ...sessionHeaders(), ... }`.
export function sessionHeaders(): Record<string, string> {
  return { 'X-Session-Id': clientId() };
}
