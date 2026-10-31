export interface IWorkflowRuntime {
  workflowId: number;
  lastTransactionId: number;
  lastState?: string | null;
  lastStateCode?: number | null;
  pendingReason?: string | null;
  agvName?: string | null;
  commandAccepted?: boolean | null;
  lastCheckedAt?: string | null;
  note?: string | null;
  startedCount: number;
}

export interface IPumpSnapshot {
  enabled: boolean;
  baseUrl: string;
  hasApiKey: boolean;
  intervalSeconds: number;
  targetRunning: number;
  runningCount: number;
  workflowIds: number[];
  runtimes: IWorkflowRuntime[];
  events: string[];
  acsReachable: boolean;
  acsVersion?: string | null;
  lastTickAtUtc?: string | null;
  nextTickAtUtc?: string | null;
  serverTimeUtc: string;
}

export interface IWorkflowSummary {
  id: number;
  name: string;
}

export interface IPumpConfigRequest {
  baseUrl?: string;
  apiKey?: string;
  intervalSeconds?: number;
  targetRunning?: number;
  workflowIds?: number[];
}
