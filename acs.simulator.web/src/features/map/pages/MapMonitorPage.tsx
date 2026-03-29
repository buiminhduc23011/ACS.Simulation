import React, { useEffect, useRef, useState } from 'react';
import { Select, Button, Space, Typography, Spin, message } from 'antd';
import { TransformWrapper, TransformComponent } from 'react-zoom-pan-pinch';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { useMapStore } from '../../../store/mapStore';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import type { IMapSummary, IMapDetail } from '../../../types/map';
import { MapCanvas } from '../components/MapCanvas';

const { Title } = Typography;

export const MapMonitorPage: React.FC = () => {
  const { maps, setMaps, activeMapDetail, setActiveMap } = useMapStore();
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const [selectedMapId, setSelectedMapId] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const [canvasSize, setCanvasSize] = useState({ width: 900, height: 600 });

  useEffect(() => {
    apiClient.get<IMapSummary[]>(ENDPOINTS.maps.list)
      .then((r) => setMaps(r.data))
      .catch(console.error);
  }, [setMaps]);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const obs = new ResizeObserver(() => {
      setCanvasSize({ width: el.clientWidth, height: el.clientHeight - 60 });
    });
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  const loadMap = async (mapId: string) => {
    setLoading(true);
    try {
      const res = await apiClient.get<IMapDetail>(ENDPOINTS.maps.detail(mapId));
      setActiveMap(res.data);
      setSelectedMapId(mapId);
    } catch {
      message.error('Failed to load map');
    } finally {
      setLoading(false);
    }
  };

  const activateMap = async () => {
    if (!selectedMapId) return;
    setLoading(true);
    try {
      await apiClient.post(ENDPOINTS.maps.activate(selectedMapId));
      message.success('Map activated — all AGVs updated');
    } catch {
      message.error('Failed to activate map');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }} ref={containerRef}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
        <Title level={4} style={{ margin: 0 }}>Map Monitor</Title>
        <Space>
          <Select
            placeholder="Select map"
            style={{ width: 260 }}
            value={selectedMapId}
            onChange={loadMap}
            options={maps.map((m) => ({ label: `${m.mapName} (${m.nodeCount}N, ${m.edgeCount}E)`, value: m.mapId }))}
          />
          <Button type="primary" disabled={!selectedMapId} loading={loading} onClick={activateMap}>
            Activate for all AGVs
          </Button>
        </Space>
      </div>

      <div style={{ flex: 1, background: '#1a1a2e', borderRadius: 8, overflow: 'hidden', position: 'relative' }}>
        {loading && (
          <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 10 }}>
            <Spin size="large" />
          </div>
        )}
        <TransformWrapper minScale={0.2} maxScale={4} initialScale={1} wheel={{ step: 0.1 }}>
          <TransformComponent wrapperStyle={{ width: '100%', height: '100%' }}>
            <MapCanvas
              map={activeMapDetail}
              agvs={agvs}
              width={canvasSize.width}
              height={canvasSize.height}
            />
          </TransformComponent>
        </TransformWrapper>
      </div>
    </div>
  );
};
