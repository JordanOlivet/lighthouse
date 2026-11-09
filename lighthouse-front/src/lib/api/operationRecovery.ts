import { getHealth } from './system';
import { operationsApi } from './operations';
import type { OperationRecoveryDeps } from '$lib/utils/connectionLoss';

/** Real API bindings for {@link withConnectionLossRecovery}. */
export const operationRecoveryDeps: OperationRecoveryDeps = {
	checkHealth: getHealth,
	listProjectOperations: (projectName: string) =>
		operationsApi.listOperationsFlat({ projectName, limit: 10 })
};
