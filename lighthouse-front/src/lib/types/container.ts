import type { EntityState } from "./global";
import type { OneShotStatus } from "./compose";

export interface Container {
  id: string;
  name: string;
  image: string;
  status: string;
  state: EntityState;
  created: string;
  labels?: Record<string, string>;
  ports?: string[];
  ipAddress?: string;
  isCrashLooping?: boolean;
  /** One-shot container (init / copy / migration task expected to exit). */
  isOneShot?: boolean;
  oneShotStatus?: OneShotStatus | null;
  exitCode?: number | null;
  /** ISO date of the end of the last run (one-shots only). */
  finishedAt?: string | null;
}

export interface ContainerDetails extends Container {
  env?: Record<string, string>;
  mounts?: Mount[];
  networks?: string[];
  portDetails?: Record<string, string>;
  ipAddress?: string;
}

export interface Mount {
  type: string;
  source: string;
  destination: string;
  readOnly: boolean;
}

export interface ContainerStats {
  cpuPercentage: number;
  memoryUsage: number;
  memoryLimit: number;
  memoryPercentage: number;
  networkRx: number;
  networkTx: number;
  diskRead: number;
  diskWrite: number;
  timestamp?: Date;
}
