// A tiny app-wide toast store. Components push messages here (most commonly
// errors) and the single <Toaster /> mounted in the root layout renders them
// as transient, stacked notifications instead of inline blocks on the page.

export type ToastKind = 'error' | 'info' | 'success';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

// How long a toast stays on screen before auto-dismissing (ms).
const DEFAULT_DURATION = 5000;

let nextId = 0;

class ToastStore {
  toasts = $state<Toast[]>([]);

  show(message: string, kind: ToastKind = 'info', duration = DEFAULT_DURATION) {
    const id = nextId++;
    this.toasts = [...this.toasts, { id, kind, message }];
    if (duration > 0) {
      setTimeout(() => this.dismiss(id), duration);
    }
    return id;
  }

  error(message: string, duration = DEFAULT_DURATION) {
    return this.show(message, 'error', duration);
  }

  info(message: string, duration = DEFAULT_DURATION) {
    return this.show(message, 'info', duration);
  }

  success(message: string, duration = DEFAULT_DURATION) {
    return this.show(message, 'success', duration);
  }

  dismiss(id: number) {
    this.toasts = this.toasts.filter((t) => t.id !== id);
  }
}

export const toasts = new ToastStore();
