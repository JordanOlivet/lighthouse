import { describe, it, expect } from 'vitest';
import { getOneShotDisplay, isIdleOneShot, formatLastRun, getComposeIdentity } from './oneShot';

describe('getOneShotDisplay', () => {
  it('maps each run status to its label and tone', () => {
    expect(getOneShotDisplay('Completed')).toEqual({ labelKey: 'compose.oneShotCompleted', tone: 'success' });
    expect(getOneShotDisplay('Failed')).toEqual({ labelKey: 'compose.oneShotFailed', tone: 'danger' });
    expect(getOneShotDisplay('Running')).toEqual({ labelKey: 'compose.oneShotRunning', tone: 'info' });
    expect(getOneShotDisplay('Pending')).toEqual({ labelKey: 'compose.oneShotPending', tone: 'neutral' });
  });

  it('surfaces an unknown status as a failure', () => {
    expect(getOneShotDisplay(null).tone).toBe('danger');
    expect(getOneShotDisplay(undefined).tone).toBe('danger');
  });
});

describe('isIdleOneShot', () => {
  it('treats completed and never-run one-shots as idle', () => {
    expect(isIdleOneShot({ isOneShot: true, oneShotStatus: 'Completed' })).toBe(true);
    expect(isIdleOneShot({ isOneShot: true, oneShotStatus: 'Pending' })).toBe(true);
  });

  it('does not hide failed or running one-shots, nor regular containers', () => {
    expect(isIdleOneShot({ isOneShot: true, oneShotStatus: 'Failed' })).toBe(false);
    expect(isIdleOneShot({ isOneShot: true, oneShotStatus: 'Running' })).toBe(false);
    expect(isIdleOneShot({ isOneShot: false, oneShotStatus: 'Completed' })).toBe(false);
    expect(isIdleOneShot({})).toBe(false);
  });
});

describe('formatLastRun', () => {
  it('returns null for missing or invalid dates', () => {
    expect(formatLastRun(null)).toBeNull();
    expect(formatLastRun(undefined)).toBeNull();
    expect(formatLastRun('not-a-date')).toBeNull();
  });

  it('formats a valid ISO date', () => {
    expect(formatLastRun('2026-11-18T18:20:51Z')).toBeTypeOf('string');
  });
});

describe('getComposeIdentity', () => {
  it('reads project and service from compose labels', () => {
    expect(
      getComposeIdentity({ 'com.docker.compose.project': 'hermes', 'com.docker.compose.service': 'agent-src' })
    ).toEqual({ project: 'hermes', service: 'agent-src' });
  });

  it('returns null outside compose', () => {
    expect(getComposeIdentity(undefined)).toBeNull();
    expect(getComposeIdentity({ 'com.docker.compose.project': 'hermes' })).toBeNull();
  });
});
