import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('$app/env', () => ({ browser: false }));
vi.mock('$lib/api/update', () => ({ updateApi: {} }));

import {
  containerUpdateState,
  hasAnyContainerUpdates,
  containersWithUpdatesCount,
  containerHasUpdate,
  setContainerUpdateResult,
  markContainerAsUpdated,
  handleContainerUpdatesCheckedEvent,
  reconcileContainerUpdateState,
  clearContainerUpdateState
} from './containerUpdate.svelte';

describe('containerUpdate store', () => {
  beforeEach(() => clearContainerUpdateState());

  it('tracks per-container update results', () => {
    setContainerUpdateResult('a', true);
    setContainerUpdateResult('b', false);

    expect(containerHasUpdate('a')).toBe(true);
    expect(containerHasUpdate('b')).toBe(false);
    expect(containerHasUpdate('unknown')).toBe(false);
    expect(hasAnyContainerUpdates.current).toBe(true);
    expect(containersWithUpdatesCount.current).toBe(1);
  });

  it('marks a container as updated', () => {
    setContainerUpdateResult('a', true);
    markContainerAsUpdated('a');
    expect(containerHasUpdate('a')).toBe(false);
    expect(hasAnyContainerUpdates.current).toBe(false);
  });

  it('applies an SSE checked event', () => {
    handleContainerUpdatesCheckedEvent({
      containersWithUpdates: 1,
      containers: [
        { containerId: 'a', updateAvailable: true },
        { containerId: 'b', updateAvailable: false }
      ]
    } as never);

    expect(containerHasUpdate('a')).toBe(true);
    expect(containerHasUpdate('b')).toBe(false);
    expect(containerUpdateState.lastChecked).toBeInstanceOf(Date);
  });

  it('reconciles away stale containers', () => {
    setContainerUpdateResult('a', true);
    setContainerUpdateResult('b', true);

    reconcileContainerUpdateState(new Set(['a']));

    expect(containerHasUpdate('a')).toBe(true);
    expect('b' in containerUpdateState.containersWithUpdates).toBe(false);
  });

  it('clears all state', () => {
    setContainerUpdateResult('a', true);
    clearContainerUpdateState();
    expect(containersWithUpdatesCount.current).toBe(0);
    expect(containerUpdateState.lastChecked).toBeNull();
  });
});
