export type AgvStatus = 'IDLE' | 'DRIVING' | 'ERROR' | 'OFFLINE' | 'CHARGING';

export const AGV_STATUS_COLORS: Record<AgvStatus, string> = {
  IDLE: '#4CAF50',
  DRIVING: '#FF9800',
  ERROR: '#F44336',
  OFFLINE: '#666666',
  CHARGING: '#5C6BC0',
};

export interface ISimAgv {
  id: string;
  serialNumber: string;
  manufacturer: string;
  ipAddress: string;
  macAddress: string;
  status: AgvStatus;
  isConnected: boolean;
  isRunning: boolean;
  posX: number;
  posY: number;
  posTheta: number;
  mapId: string;
  batteryLevel: number;
  isCharging: boolean;
  hasErrors: boolean;
  errorCount: number;
  speedX: number;
  speedY: number;
  errors?: IAgvError[];
}

export interface IAgvState {
  serialNumber: string;
  driving: boolean;
  paused: boolean;
  posX: number;
  posY: number;
  theta: number;
  mapId: string;
  batteryLevel: number;
  isCharging: boolean;
  operatingMode: string;
  errors: IAgvError[];
  safetyStop: boolean;
}

export interface IAgvError {
  errorType: string;
  errorLevel: string;
  description: string | null;
}

export interface ICreateAgvRequest {
  serialNumber: string;
  manufacturer: string;
  ipAddress?: string;
  macAddress?: string;
  posX: number;
  posY: number;
  posTheta: number;
  mapId: string;
  vehicleShapeId?: number; // 1=CARRIER, 2=FORKLIFT, 3=CONVEYOR, 4=TUGGER
  navigationTechnologyId?: number; // 1=NATURAL, 2=LASER, 3=MAGNETIC, 4=QR
}
