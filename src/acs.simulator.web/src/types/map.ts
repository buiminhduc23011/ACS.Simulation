export interface IMapSummary {
  id: number;
  mapId: string;
  mapName: string;
  mapDescription?: string;
  nodeCount: number;
  edgeCount: number;
  stationCount: number;
}

export interface IMapDetail {
  id: number;
  mapId: string;
  mapName: string;
  mapDescription?: string;
  nodes: IMapNode[];
  edges: IMapEdge[];
  stations: IMapStation[];
}

export interface IMapNode {
  id: number;
  nodeId: string;
  x: number;
  y: number;
  nodeDescription?: string;
}

export interface IMapEdge {
  id: number;
  edgeId: string;
  startNodeId: string;
  endNodeId: string;
  length?: number;
  maxSpeed?: number;
}

export interface IMapStation {
  id: number;
  stationId: string;
  stationName?: string;
  interactionNodeIds?: string;
  positionX?: number;
  positionY?: number;
}
