import { describe, it, expect, vi } from 'vitest';
import { AxiosError, AxiosHeaders } from 'axios';
import type { Operation } from '$lib/types';
import {
	isConnectionLostError,
	recoverOperationOutcome,
	withConnectionLossRecovery,
	ConnectionLostError,
	type OperationRecoveryDeps
} from './connectionLoss';

function axiosError(status?: number, code?: string): AxiosError {
	const config = { headers: new AxiosHeaders() };
	const response =
		status === undefined
			? undefined
			: { status, statusText: '', headers: {}, config, data: '' };
	return new AxiosError('boom', code, config, undefined, response);
}

function op(partial: Partial<Operation>): Operation {
	return {
		operationId: 'op-1',
		type: 'compose_restart',
		status: 'completed',
		projectName: 'traefik',
		startedAt: new Date(1_000_000).toISOString(),
		...partial
	} as Operation;
}

/** Deps with a fake clock: sleep advances time instead of waiting. */
function fakeDeps(
	listProjectOperations: OperationRecoveryDeps['listProjectOperations'],
	checkHealth: OperationRecoveryDeps['checkHealth'] = async () => ({})
): OperationRecoveryDeps {
	let clock = 1_000_000;
	return {
		checkHealth,
		listProjectOperations,
		now: () => clock,
		sleep: async (ms) => {
			clock += ms;
		}
	};
}

describe('isConnectionLostError', () => {
	it('treats network errors and proxy/CDN statuses as connection loss', () => {
		expect(isConnectionLostError(axiosError(undefined, 'ERR_NETWORK'))).toBe(true);
		expect(isConnectionLostError(axiosError(502))).toBe(true);
		expect(isConnectionLostError(axiosError(520))).toBe(true);
		expect(isConnectionLostError(axiosError(524))).toBe(true);
	});

	it('does not treat backend errors, timeouts or non-axios errors as connection loss', () => {
		expect(isConnectionLostError(axiosError(500))).toBe(false);
		expect(isConnectionLostError(axiosError(400))).toBe(false);
		expect(isConnectionLostError(axiosError(undefined, 'ECONNABORTED'))).toBe(false);
		expect(isConnectionLostError(new Error('x'))).toBe(false);
	});
});

describe('recoverOperationOutcome', () => {
	const options = { type: 'compose_restart', projectName: 'traefik', startedAt: 1_000_000 };

	it('waits for the backend, then returns the completed operation', async () => {
		let healthCalls = 0;
		const deps = fakeDeps(
			async () => [op({})],
			async () => {
				healthCalls++;
				if (healthCalls < 3) throw new Error('unreachable');
				return {};
			}
		);

		const outcome = await recoverOperationOutcome(options, deps);

		expect(outcome.status).toBe('completed');
		expect(healthCalls).toBe(3);
	});

	it('keeps polling while the operation is running or not recorded yet', async () => {
		const list = vi
			.fn<OperationRecoveryDeps['listProjectOperations']>()
			.mockResolvedValueOnce([])
			.mockResolvedValueOnce([op({ status: 'running' })])
			.mockResolvedValue([op({ status: 'failed', errorMessage: 'pull failed' })]);

		const outcome = await recoverOperationOutcome(options, fakeDeps(list));

		expect(outcome).toMatchObject({ status: 'failed', operation: { errorMessage: 'pull failed' } });
		expect(list).toHaveBeenCalledTimes(3);
	});

	it('ignores operations of another type or older than the request', async () => {
		const deps = fakeDeps(async () => [
			op({ type: 'compose_update' }),
			op({ startedAt: new Date(1_000_000 - 120_000).toISOString() })
		]);

		const outcome = await recoverOperationOutcome({ ...options, timeoutMs: 10_000 }, deps);

		expect(outcome.status).toBe('unknown');
	});

	it('returns unknown when the backend never comes back', async () => {
		const deps = fakeDeps(
			async () => [op({})],
			async () => {
				throw new Error('unreachable');
			}
		);

		const outcome = await recoverOperationOutcome({ ...options, timeoutMs: 10_000 }, deps);

		expect(outcome.status).toBe('unknown');
	});
});

describe('withConnectionLossRecovery', () => {
	const options = { type: 'compose_restart', projectName: 'traefik', timeoutMs: 10_000 };

	it('returns the action result when nothing goes wrong', async () => {
		const result = await withConnectionLossRecovery(async () => 42, options, fakeDeps(async () => []));
		expect(result).toBe(42);
	});

	it('rethrows non connection-loss errors unchanged', async () => {
		const error = axiosError(500);
		await expect(
			withConnectionLossRecovery(async () => Promise.reject(error), options, fakeDeps(async () => []))
		).rejects.toBe(error);
	});

	it('resolves when the operation completed despite the connection loss', async () => {
		const onConnectionLost = vi.fn();
		const result = await withConnectionLossRecovery(
			async () => Promise.reject(axiosError(520)),
			{ ...options, onConnectionLost },
			fakeDeps(async () => [op({})])
		);

		expect(result).toBeUndefined();
		expect(onConnectionLost).toHaveBeenCalledOnce();
	});

	it('rejects with the operation error when it failed', async () => {
		await expect(
			withConnectionLossRecovery(
				async () => Promise.reject(axiosError(520)),
				options,
				fakeDeps(async () => [op({ status: 'failed', errorMessage: 'compose error' })])
			)
		).rejects.toThrow('compose error');
	});

	it('rejects with ConnectionLostError when the outcome stays unknown', async () => {
		await expect(
			withConnectionLossRecovery(
				async () => Promise.reject(axiosError(undefined, 'ERR_NETWORK')),
				options,
				fakeDeps(async () => [])
			)
		).rejects.toBeInstanceOf(ConnectionLostError);
	});
});
