export type ScenarioStatus = 'idle' | 'running' | 'completed' | 'failed' | 'stopped';

export interface IScenarioStep {
  type: string;
  agvId?: string;
  durationMs?: number;
  x?: number;
  y?: number;
  theta?: number;
  batteryLevel?: number;
  speed?: number;
  errorType?: string;
  errorDescription?: string;
  errorLevel?: string;
  condition?: string;
  message?: string;
  latencyMinMs?: number;
  latencyMaxMs?: number;
  packetLossPercent?: number;
  [key: string]: unknown;
}

export interface IScenario {
  id: string;
  name: string;
  description?: string;
  steps: IScenarioStep[];
  tags?: string[];
  createdAt?: string;
}

export interface IStepResult {
  stepIndex: number;
  stepType: string;
  success: boolean;
  message?: string;
  durationMs: number;
  timestamp: string;
}

export interface IScenarioRun {
  scenarioId: string;
  scenarioName: string;
  status: ScenarioStatus;
  startedAt: string;
  completedAt?: string;
  totalSteps: number;
  currentStep: number;
  stepResults: IStepResult[];
}
