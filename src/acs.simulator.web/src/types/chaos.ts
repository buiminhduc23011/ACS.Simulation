export interface IErrorTemplate {
  id: string;
  name: string;
  description?: string;
  errorType: string;
  errorLevel: 'WARNING' | 'FATAL';
  errorDescription: string;
}

export interface IChaosSettings {
  latencyEnabled: boolean;
  latencyMinMs: number;
  latencyMaxMs: number;
  packetLossEnabled: boolean;
  packetLossPercent: number;
}
