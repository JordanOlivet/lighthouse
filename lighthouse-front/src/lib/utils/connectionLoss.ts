import { isAxiosError } from 'axios';
import type { Operation } from '$lib/types';

/**
 * Upstream statuses returned by a reverse proxy / CDN when the origin goes away mid-request
 * (Bad Gateway, Service Unavailable, Gateway Timeout, Cloudflare 520-527).
 */
const PROXY_ERROR_STATUSES = new Set([502, 503, 504, 520, 521, 522, 523, 524, 525, 526, 527]);

/**
 * True when the request failed because the connection to Lighthouse was lost, as opposed to
 * the backend answering with an error. Typical cause: the project being restarted or updated is
 * the reverse proxy that serves Lighthouse itself, so the in-flight request is cut.
 */
export function isConnectionLostError(error: unknown): boolean {
	if (!isAxiosError(error)) return false;
	if (!error.response) {
		// No response at all (network error, socket closed). A client-side timeout is not a
		// connection loss: the backend may still be working on it.
		return error.code !== 'ECONNABORTED' && error.code !== 'ERR_CANCELED';
	}
	return PROXY_ERROR_STATUSES.has(error.response.status);
}

/** Thrown when the connection was lost and the operation's outcome could not be confirmed. */
export class ConnectionLostError extends Error {
	constructor(message = 'Connection lost; operation result unknown') {
		super(message);
		this.name = 'ConnectionLostError';
	}
}

export interface OperationRecoveryDeps {
	/** Resolves when the backend answers (throws while it is unreachable). */
	checkHealth: () => Promise<unknown>;
	/** Lists the most recent operations of a project, newest first. */
	listProjectOperations: (projectName: string) => Promise<Operation[]>;
	sleep?: (ms: number) => Promise<void>;
	now?: () => number;
}

export interface OperationRecoveryOptions {
	/** Operation type to look for (see `OperationType`). */
	type: string;
	projectName: string;
	/** Client timestamp (ms) taken just before the request was sent. */
	startedAt: number;
	/** Total time budget for the recovery, in ms. */
	timeoutMs?: number;
	/** Delay between polls, in ms. */
	pollIntervalMs?: number;
	/** Tolerated client/server clock skew when matching the operation start time, in ms. */
	clockSkewMs?: number;
}

export type RecoveredOutcome =
	| { status: 'completed'; operation: Operation }
	| { status: 'failed'; operation: Operation }
	| { status: 'unknown' };

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * After a connection loss, waits for the backend to be reachable again and reads the outcome of
 * the operation from the operation history. Returns `unknown` if the backend stays unreachable or
 * no finished matching operation shows up within the time budget.
 */
export async function recoverOperationOutcome(
	options: OperationRecoveryOptions,
	deps: OperationRecoveryDeps
): Promise<RecoveredOutcome> {
	const sleep = deps.sleep ?? defaultSleep;
	const now = deps.now ?? Date.now;
	const timeoutMs = options.timeoutMs ?? 90_000;
	const pollIntervalMs = options.pollIntervalMs ?? 2_000;
	const clockSkewMs = options.clockSkewMs ?? 30_000;
	const deadline = now() + timeoutMs;
	const since = options.startedAt - clockSkewMs;

	while (now() < deadline) {
		try {
			await deps.checkHealth();
			const operations = await deps.listProjectOperations(options.projectName);
			const match = operations.find(
				(op) =>
					op.type === options.type &&
					op.projectName === options.projectName &&
					Date.parse(op.startedAt) >= since
			);
			if (match?.status === 'completed') return { status: 'completed', operation: match };
			if (match?.status === 'failed' || match?.status === 'cancelled') {
				return { status: 'failed', operation: match };
			}
			// Not recorded yet, or still running: keep polling.
		} catch {
			// Backend still unreachable: keep polling.
		}
		await sleep(pollIntervalMs);
	}

	return { status: 'unknown' };
}

/**
 * Runs `action`; if it fails because the connection was lost, recovers the real outcome from the
 * operation history instead of reporting a false failure.
 * - completed → resolves with `undefined`
 * - failed → rejects with the operation's error message
 * - unknown → rejects with {@link ConnectionLostError}
 * Any other error is rethrown unchanged.
 */
export async function withConnectionLossRecovery<T>(
	action: () => Promise<T>,
	options: Omit<OperationRecoveryOptions, 'startedAt'> & { onConnectionLost?: () => void },
	deps: OperationRecoveryDeps
): Promise<T | undefined> {
	const startedAt = (deps.now ?? Date.now)();
	try {
		return await action();
	} catch (error) {
		if (!isConnectionLostError(error)) throw error;

		options.onConnectionLost?.();
		const outcome = await recoverOperationOutcome({ ...options, startedAt }, deps);
		if (outcome.status === 'completed') return undefined;
		if (outcome.status === 'failed') {
			throw new Error(outcome.operation.errorMessage || 'Operation failed');
		}
		throw new ConnectionLostError();
	}
}
