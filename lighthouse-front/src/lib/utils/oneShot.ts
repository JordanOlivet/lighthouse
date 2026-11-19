import type { OneShotStatus } from '$lib/types/compose';

/** Visual tone of a one-shot status badge. */
export type OneShotTone = 'success' | 'danger' | 'info' | 'neutral';

export interface OneShotDisplay {
  /** i18n key of the status label (under `compose.`). */
  labelKey: string;
  tone: OneShotTone;
}

/**
 * Maps a one-shot run status to its badge label and tone:
 * Completed → "Terminé" (green), Failed → "Échec" (red), Running → "En cours" (blue),
 * Pending → "Jamais exécuté" (gray). Unknown statuses are shown as failed so they surface.
 */
export function getOneShotDisplay(status: OneShotStatus | null | undefined): OneShotDisplay {
  switch (status) {
    case 'Completed':
      return { labelKey: 'compose.oneShotCompleted', tone: 'success' };
    case 'Running':
      return { labelKey: 'compose.oneShotRunning', tone: 'info' };
    case 'Pending':
      return { labelKey: 'compose.oneShotPending', tone: 'neutral' };
    default:
      return { labelKey: 'compose.oneShotFailed', tone: 'danger' };
  }
}

/**
 * Whether a one-shot is idle by design (completed with code 0, or never run): it must not
 * be shown or counted as a stopped container.
 */
export function isIdleOneShot(item: { isOneShot?: boolean; oneShotStatus?: OneShotStatus | null }): boolean {
  return !!item.isOneShot && (item.oneShotStatus === 'Completed' || item.oneShotStatus === 'Pending');
}

/** Formats the end of the last run for display, or null when unknown. */
export function formatLastRun(finishedAt: string | null | undefined): string | null {
  if (!finishedAt) return null;
  const date = new Date(finishedAt);
  return Number.isNaN(date.getTime()) ? null : date.toLocaleString();
}

/** Compose project and service names of a container, from its compose labels. */
export function getComposeIdentity(
  labels: Record<string, string> | undefined
): { project: string; service: string } | null {
  const project = labels?.['com.docker.compose.project'];
  const service = labels?.['com.docker.compose.service'];
  return project && service ? { project, service } : null;
}
