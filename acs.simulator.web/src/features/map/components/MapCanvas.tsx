import React, { useMemo } from 'react';
import { Stage, Layer, Circle, Line, Text, Group, Arrow } from 'react-konva';
import type { IMapDetail } from '../../../types/map';
import type { ISimAgv } from '../../../types/agv';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import { METERS_TO_PIXELS } from '../../../config/config';

interface Props {
  map: IMapDetail | null;
  agvs: ISimAgv[];
  width: number;
  height: number;
}

const OFFSET_X = 40;
const OFFSET_Y = 40;

export const MapCanvas: React.FC<Props> = ({ map, agvs, width, height }) => {
  const nodeMap = useMemo(() => {
    if (!map) return new Map<string, { x: number; y: number }>();
    return new Map(map.nodes.map((n) => [n.nodeId, { x: n.x, y: n.y }]));
  }, [map]);

  const toPixel = (v: number) => v * METERS_TO_PIXELS;

  if (!map) {
    return (
      <Stage width={width} height={height}>
        <Layer>
          <Text text="No map selected" x={width / 2 - 60} y={height / 2} fill="#888" fontSize={16} />
        </Layer>
      </Stage>
    );
  }

  return (
    <Stage width={width} height={height}>
      {/* Edges */}
      <Layer>
        {map.edges.map((edge) => {
          const from = nodeMap.get(edge.startNodeId);
          const to = nodeMap.get(edge.endNodeId);
          if (!from || !to) return null;
          return (
            <Arrow
              key={edge.edgeId}
              points={[
                OFFSET_X + toPixel(from.x), OFFSET_Y + toPixel(from.y),
                OFFSET_X + toPixel(to.x), OFFSET_Y + toPixel(to.y),
              ]}
              stroke="#334"
              strokeWidth={1.5}
              fill="#334"
              pointerLength={8}
              pointerWidth={6}
            />
          );
        })}
      </Layer>

      {/* Nodes */}
      <Layer>
        {map.nodes.map((node) => {
          const isStation = map.stations.some((s) =>
            s.interactionNodeIds?.includes(node.nodeId)
          );
          return (
            <Group key={node.nodeId} x={OFFSET_X + toPixel(node.x)} y={OFFSET_Y + toPixel(node.y)}>
              <Circle
                radius={isStation ? 10 : 6}
                fill={isStation ? '#5C6BC0' : '#37474F'}
                stroke={isStation ? '#9FA8DA' : '#546E7A'}
                strokeWidth={1}
              />
              {isStation && (
                <Text text={node.nodeId} x={-20} y={14} fontSize={9} fill="#9FA8DA" width={40} align="center" />
              )}
            </Group>
          );
        })}
      </Layer>

      {/* AGVs */}
      <Layer>
        {agvs.filter((a) => a.posX != null && a.posY != null).map((agv) => {
          const px = OFFSET_X + toPixel(agv.posX ?? 0);
          const py = OFFSET_Y + toPixel(agv.posY ?? 0);
          const color = AGV_STATUS_COLORS[agv.status];
          const angle = ((agv.posTheta ?? 0) * 180) / Math.PI;

          return (
              <Group key={agv.id} x={px} y={py} rotation={angle}>
              {/* Body */}
              <Line
                points={[-10, -7, 10, -7, 14, 0, 10, 7, -10, 7]}
                closed
                fill={color}
                stroke="#fff"
                strokeWidth={1}
                opacity={agv.status === 'OFFLINE' ? 0.4 : 1}
              />
              {/* Direction arrow */}
              <Arrow points={[0, 0, 16, 0]} stroke="#fff" strokeWidth={2} fill="#fff" pointerLength={5} pointerWidth={4} />
              {/* Label */}
              <Text text={agv.id} x={-30} y={10} fontSize={10} fill="#fff" width={60} align="center" />
            </Group>
          );
        })}
      </Layer>
    </Stage>
  );
};
