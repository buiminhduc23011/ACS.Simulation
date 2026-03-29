import React, { useState, useEffect } from 'react';
import { Drawer, Tabs, Form, Input, InputNumber, Button, Space, Select, Tag, Divider, Alert, AutoComplete } from 'antd';
import type { ISimAgv } from '../../../types/agv';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import { useFleetApi } from '../hooks/useFleetApi';
import type { IMapSummary } from '../../../types/map';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';

interface Props {
  agv: ISimAgv | null;
  onClose: () => void;
}

export const AgvDetailDrawer: React.FC<Props> = ({ agv, onClose }) => {
  const { setPosition, setBattery, setSpeed, addError, clearErrors, disconnect, startAgv, stopAgv } = useFleetApi();
  const [posForm] = Form.useForm();
  const [batForm] = Form.useForm();
  const [errForm] = Form.useForm();
  const [loading, setLoading] = useState<string | null>(null);
  const [mapOptions, setMapOptions] = useState<{ value: string; label: string }[]>([]);

  useEffect(() => {
    apiClient.get<IMapSummary[]>(ENDPOINTS.maps.list)
      .then(res => setMapOptions(res.data.map(m => ({ value: m.mapId, label: `${m.mapId} — ${m.mapName}` }))))
      .catch(() => { /* ACS not connected, options stay empty */ });
  }, []);

  if (!agv) return null;

  const run = async (key: string, fn: () => Promise<void>) => {
    setLoading(key);
    try { await fn(); } catch { /* handled by interceptor */ } finally { setLoading(null); }
  };

  return (
    <Drawer
      title={`AGV Control — ${agv.id}`}
      open={!!agv}
      onClose={onClose}
      width={480}
      extra={
        <Space>
          <Tag color={AGV_STATUS_COLORS[agv.status]}>{agv.status}</Tag>
          <Button size="small" onClick={() => run('start', () => startAgv(agv.id))}>Start</Button>
          <Button size="small" danger onClick={() => run('stop', () => stopAgv(agv.id))}>Stop</Button>
        </Space>
      }
    >
      <div style={{ marginBottom: 12 }}>
        <b>Position:</b> ({agv.posX?.toFixed(2)}, {agv.posY?.toFixed(2)}) θ={agv.posTheta?.toFixed(2)} rad |{' '}
        <b>Battery:</b> {agv.batteryLevel?.toFixed(0)}% |{' '}
        <b>Speed:</b> {Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0).toFixed(2)} m/s
      </div>
      {agv.hasErrors && (
        <Alert
          type="error"
          message={`${agv.errorCount} active error(s)`}
          description={agv.errors?.map((e, i) => <div key={i}>{e.errorType}: {e.description}</div>)}
          style={{ marginBottom: 12 }}
        />
      )}

      <Tabs
        items={[
          {
            key: 'position',
            label: 'Position',
            children: (
              <Form form={posForm} layout="vertical" initialValues={{ posX: agv.posX, posY: agv.posY, posTheta: agv.posTheta, mapId: agv.mapId }}
                onFinish={(v) => run('pos', () => setPosition(agv.id, v.posX, v.posY, v.posTheta, v.mapId ?? ''))}>
                <div style={{ display: 'flex', gap: 8 }}>
                  <Form.Item name="posX" label="X" style={{ flex: 1 }}><InputNumber step={0.5} style={{ width: '100%' }} /></Form.Item>
                  <Form.Item name="posY" label="Y" style={{ flex: 1 }}><InputNumber step={0.5} style={{ width: '100%' }} /></Form.Item>
                  <Form.Item name="posTheta" label="θ (rad)" style={{ flex: 1 }}><InputNumber step={0.1} min={-Math.PI} max={Math.PI} style={{ width: '100%' }} /></Form.Item>
                </div>
                <Form.Item name="mapId" label="Map ID" rules={[{ required: true, message: 'Map ID is required' }]}>
                  <AutoComplete
                    options={mapOptions}
                    placeholder={mapOptions.length ? 'Select or type map ID' : 'Type map ID (e.g. floor1)'}
                    filterOption={(input, opt) => (opt?.value ?? '').toLowerCase().includes(input.toLowerCase())}
                    allowClear
                  />
                </Form.Item>
                <Form.Item><Button htmlType="submit" type="primary" loading={loading === 'pos'}>Set Position</Button></Form.Item>
              </Form>
            ),
          },
          {
            key: 'battery',
            label: 'Battery',
            children: (
              <Form form={batForm} layout="inline" initialValues={{ batteryLevel: agv.batteryLevel, charging: false }}
                onFinish={(v) => run('bat', () => setBattery(agv.id, v.batteryLevel, v.charging))}>
                <Form.Item name="batteryLevel" label="Level %"><InputNumber min={0} max={100} /></Form.Item>
                <Form.Item name="charging" label="Charging">
                  <Select style={{ width: 90 }} options={[{ label: 'Yes', value: true }, { label: 'No', value: false }]} />
                </Form.Item>
                <Form.Item><Button htmlType="submit" type="primary" loading={loading === 'bat'}>Set</Button></Form.Item>
              </Form>
            ),
          },
          {
            key: 'speed',
            label: 'Speed',
            children: (
              <Form layout="inline" initialValues={{ speed: Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0) }}
                onFinish={(v) => run('spd', () => setSpeed(agv.id, v.speed))}>
                  <Form.Item name="speed" label="Speed m/s"><InputNumber min={0} max={10} step={0.1} /></Form.Item>
                <Form.Item><Button htmlType="submit" type="primary" loading={loading === 'spd'}>Set</Button></Form.Item>
              </Form>
            ),
          },
          {
            key: 'errors',
            label: 'Errors',
            children: (
              <Space direction="vertical" style={{ width: '100%' }}>
                <Form form={errForm} layout="vertical"
                  onFinish={(v) => run('err', () => addError(agv.id, v.errorType, v.errorDescription, v.errorLevel))}>
                  <Form.Item name="errorType" label="Error Type" rules={[{ required: true }]}>
                    <Input placeholder="e.g. EMERGENCY_STOP" />
                  </Form.Item>
                  <Form.Item name="errorDescription" label="Description" rules={[{ required: true }]}>
                    <Input placeholder="Description..." />
                  </Form.Item>
                  <Form.Item name="errorLevel" label="Level" initialValue="WARNING">
                    <Select options={[{ label: 'WARNING', value: 'WARNING' }, { label: 'FATAL', value: 'FATAL' }]} />
                  </Form.Item>
                  <Button htmlType="submit" type="primary" loading={loading === 'err'}>Inject Error</Button>
                </Form>
                <Divider />
                <Button danger onClick={() => run('clr', () => clearErrors(agv.id))} loading={loading === 'clr'}>
                  Clear All Errors
                </Button>
              </Space>
            ),
          },
          {
            key: 'network',
            label: 'Network',
            children: (
              <Button danger onClick={() => run('disc', () => disconnect(agv.id))} loading={loading === 'disc'}>
                Trigger Disconnect
              </Button>
            ),
          },
        ]}
      />
    </Drawer>
  );
};
