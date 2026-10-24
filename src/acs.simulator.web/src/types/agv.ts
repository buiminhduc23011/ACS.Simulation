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
  holdActions?: boolean;
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
  loadCount: number;
  operatingMode?: string;
  orderId?: string;
  orderUpdateId?: number;
  vehicleShapeId?: number; // 1=CARRIER, 2=FORKLIFT, 3=CONVEYOR, 4=TUGGER
  navigationTechnologyId?: number; // 1=NATURAL, 2=LASER, 3=MAGNETIC, 4=QR
  errors?: IAgvError[];
  mapMappings?: IAgvMapMapping[];
  activeNotification?: IScreenNotification | null;
}

/** Notification currently shown on the AGV screen (ACS → notifications topic). */
export interface IScreenNotification {
  type: string;
  message: string;
  expiresAtUtc?: string | null;
}

/** ACS → AGV MQTT payload captured by simulator for control-page debug. */
export interface IInboundMqttMessage {
  timestamp: string;
  topicType: 'order' | 'instantActions' | 'notifications' | string;
  topic: string;
  payload: string;
  accepted: boolean;
  note?: string | null;
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

export interface ISimMapInfo {
  mapId: string;
  mapName?: string;
  nodeCount: number;
  edgeCount: number;
}

export interface ISimMapNode {
  nodeId: string;
  x: number;
  y: number;
  nodeDescription?: string | null;
  rfidTag?: string | null;
}

export interface IAgvMapMapping {
  sourceMapId: string;
  targetMapId: string;
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
  mapMappings?: IAgvMapMapping[];
}

export interface IBulkCreateRequest {
  count: number;
  serialPrefix: string;
  startIndex?: number;
  manufacturer?: string;
  mapId?: string;
  originX?: number;
  originY?: number;
  theta?: number;
  spacing?: number;
  columns?: number;
  serialNumberPadWidth?: number;
}

export interface IBulkCreateResult {
  created: number;
  failed: number;
  serials: string[];
  errors: string[];
}

export interface IBulkOperationResult {
  started?: number;
  stopped?: number;
  deleted?: number;
  total?: number;
}
