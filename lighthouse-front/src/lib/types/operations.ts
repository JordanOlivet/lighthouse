// Operation Types
export interface Operation {
  operationId: string;
  type: OperationType;
  status: OperationStatus;
  progress?: number;
  userId?: number;
  projectPath?: string;
  projectName?: string;
  containerId?: string;
  containerName?: string;
  username?: string;
  startedAt: string;
  completedAt?: string;
  errorMessage?: string;
  isAcknowledged?: boolean;
}

export interface OperationDetails extends Operation {
  logs?: string;
}

export interface OperationProgress {
  operationId: string;
  status: OperationStatus;
  progress: number;
  message?: string;
  timestamp: string;
}

// Operation Request/Response Types
export interface CreateOperationRequest {
  type: OperationType;
  projectPath?: string;
  projectName?: string;
}

export interface UpdateOperationStatusRequest {
  status: OperationStatus;
  progress?: number;
  logs?: string;
  errorMessage?: string;
}

export interface OperationFilterRequest {
  type?: OperationType;
  status?: OperationStatus;
  userId?: number;
  projectName?: string;
  startDate?: string;
  endDate?: string;
  page?: number;
  pageSize?: number;
}

export interface OperationsListResponse {
  operations: Operation[];
  totalCount: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export interface ActiveOperationsCountResponse {
  count: number;
}

// Operation Type
export const OperationType = {
  ComposeUp: 'compose_up',
  ComposeDown: 'compose_down',
  ComposeRestart: 'compose_restart',
  ComposeStop: 'compose_stop',
  ComposeRerun: 'compose_rerun',
  ContainerStart: 'container_start',
  ContainerStop: 'container_stop',
  ContainerRestart: 'container_restart',
  ContainerRemove: 'container_remove',
  ContainerPause: 'container_pause',
  ContainerUnpause: 'container_unpause',
  ComposeUpdate: 'compose_update',
  ContainerUpdate: 'container_update'
} as const;

export type OperationType = typeof OperationType[keyof typeof OperationType];

// Operation Status
export const OperationStatus = {
  Pending: 'pending',
  Running: 'running',
  Completed: 'completed',
  Failed: 'failed',
  Cancelled: 'cancelled'
} as const;

export type OperationStatus = typeof OperationStatus[keyof typeof OperationStatus];

// SSE Operation Update Event
export interface OperationUpdateEvent {
  operationId: string;
  status: OperationStatus;
  progress: number;
  logs?: string;
  errorMessage?: string;
  timestamp?: string;
  type?: string;
  projectName?: string;
  projectPath?: string;
  containerId?: string;
  containerName?: string;
}
