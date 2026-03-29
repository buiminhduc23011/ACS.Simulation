export interface IErrorTemplate {
  id: string;
  name: string;
  description?: string;
  errorType: string;
  errorLevel: 'WARNING' | 'FATAL';
  errorDescription: string;
}

export interface IChaosSettings {
  latencyMinMs: number;
  latencyMaxMs: number;
  packetLossPercent: number;
}
