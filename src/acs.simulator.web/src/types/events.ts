import type { AgvStatus } from './agv';

export type FleetEventType =
  | 'AgvCreated'
  | 'AgvDeleted'
  | 'AgvStarted'
  | 'AgvStopped'
  | 'StatusChanged'
  | 'ErrorAdded'
  | 'ErrorsCleared'
  | 'BatteryChanged'
  | 'PositionChanged'
  | 'MapActivated'
  | 'ChaosApplied'
  | 'Disconnected';

export interface IFleetEvent {
  eventType: FleetEventType;
  agvId: string;
  timestamp: string;
  data?: Record<string, unknown>;
  previousStatus?: AgvStatus;
  newStatus?: AgvStatus;
  message?: string;
}

export interface ILogMessage {
  agvId: string;
  level: 'Info' | 'Warning' | 'Error' | 'Debug';
  message: string;
  timestamp: string;
}

export interface ISignalRFleetUpdate {
  agvs: import('./agv').ISimAgv[];
  timestamp: string;
}
