import React, { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  Card, Button, Space, Tag, Slider, InputNumber, Form, Input, Select,
  Divider, Row, Col, Typography, Progress, Alert, Descriptions, Badge,
} from 'antd';
import {
  ArrowLeftOutlined, PlayCircleOutlined, StopOutlined,
  VerticalAlignTopOutlined, VerticalAlignBottomOutlined,
  ThunderboltOutlined, DeleteOutlined, WarningOutlined,
} from '@ant-design/icons';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useFleetApi } from '../hooks/useFleetApi';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import { VirtualJoystick } from '../components/VirtualJoystick';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';

const { Title, Text } = Typography;

const ERROR_TEMPLATES = [
  { name: 'SENSOR_FAILURE', label: 'Sensor Failure', color: '#faad14' },
  { name: 'MOTOR_OVERHEAT', label: 'Motor Overheat', color: '#fa8c16' },
  { name: 'OBSTACLE_DETECTED', label: 'Obstacle', color: '#fa541c' },
  { name: 'EMERGENCY_STOP', label: 'E-Stop', color: '#f5222d' },
  { name: 'LOCALIZATION_LOST', label: 'Localization Lost', color: '#cf1322' },
  { name: 'BATTERY_LOW', label: 'Battery Low', color: '#faad14' },
  { name: 'LOAD_DROPPED', label: 'Load Dropped', color: '#f5222d' },
  { name: 'COMMUNICATION_ERROR', label: 'Comm Error', color: '#fa8c16' },
];

const TURN_STEP_PER_TICK = Math.PI / 18;

function normalizeAngle(angle: number): number {
  let normalized = angle;
  while (normalized > Math.PI) normalized -= Math.PI * 2;
  while (normalized < -Math.PI) normalized += Math.PI * 2;
  return normalized;
}

export const AgvControlPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const agv = useFleetStore(useShallow((s) => (id ? s.agvs[id] : undefined)));
  const {
    fetchFleet, startAgv, stopAgv, setBattery, setSpeed,
    addError, clearErrors, injectTemplate, liftAgv, lowerAgv, disconnect,
  } = useFleetApi();

  const [loading, setLoading] = useState<string | null>(null);
  const [moveSpeed, setMoveSpeed] = useState(1.0);
  const [driveDirection, setDriveDirection] = useState<'IDLE' | 'FORWARD' | 'REVERSE' | 'TURNING'>('IDLE');
  const [errForm] = Form.useForm();

  // Keep a ref to the latest AGV position for joystick delta calculations
  const posRef = useRef({ x: 0, y: 0, theta: 0, mapId: '' });
  useEffect(() => {
    if (agv) {
      posRef.current = { x: agv.posX, y: agv.posY, theta: agv.posTheta, mapId: agv.mapId ?? '' };
    }
  }, [agv]);

  useEffect(() => { fetchFleet(); }, [fetchFleet]);

  // Poll for state updates
  useEffect(() => {
    const interval = setInterval(fetchFleet, 2000);
    return () => clearInterval(interval);
  }, [fetchFleet]);

  const run = async (key: string, fn: () => Promise<void>) => {
    setLoading(key);
    try { await fn(); } catch { /* handled by interceptor */ } finally { setLoading(null); }
  };

  const handleJoystickMove = useCallback((throttle: number, steering: number) => {
    if (!id) return;
    const cur = posRef.current;
    const newTheta = normalizeAngle(cur.theta + steering * TURN_STEP_PER_TICK);
    const distanceStep = throttle * moveSpeed * 0.05;
    const newX = cur.x + Math.cos(newTheta) * distanceStep;
    const newY = cur.y + Math.sin(newTheta) * distanceStep;
    posRef.current = { x: newX, y: newY, theta: newTheta, mapId: cur.mapId };
    setDriveDirection(
      throttle > 0.05 ? 'FORWARD' :
      throttle < -0.05 ? 'REVERSE' :
      Math.abs(steering) > 0.05 ? 'TURNING' :
      'IDLE'
    );
    apiClient.post(ENDPOINTS.control.position(id), {
      x: newX, y: newY, theta: newTheta, mapId: cur.mapId,
    }).catch(() => {});
  }, [id, moveSpeed]);

  const handleJoystickEnd = useCallback(() => {
    setDriveDirection('IDLE');
  }, []);

  if (!id) return <div>No AGV ID specified</div>;
  if (!agv) {
    return (
      <div style={{ padding: 24 }}>
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/fleet')}>Back to Fleet</Button>
        <Alert type="warning" message={`AGV "${id}" not found. It may not have been created yet.`} style={{ marginTop: 16 }} />
      </div>
    );
  }

  const batteryColor = agv.batteryLevel > 50 ? '#52c41a' : agv.batteryLevel > 20 ? '#faad14' : '#ff4d4f';
  const hasLoad = (agv as any).loads?.length > 0 || false;
  const planarSpeed = Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0);
  const driveTagColor =
    driveDirection === 'FORWARD' ? 'green' :
    driveDirection === 'REVERSE' ? 'orange' :
    driveDirection === 'TURNING' ? 'blue' :
    'default';

  return (
    <div style={{ padding: '0 8px' }}>
      {/* Header */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 16 }}>
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/fleet')} />
        <Title level={4} style={{ margin: 0 }}>AGV Control: {agv.serialNumber}</Title>
        <Tag color={AGV_STATUS_COLORS[agv.status]} style={{ fontSize: 14, padding: '2px 12px' }}>{agv.status}</Tag>
        {agv.hasErrors && <Badge count={agv.errorCount} style={{ marginLeft: 4 }} />}
      </div>

      <Row gutter={[16, 16]}>
        {/* Left column: Status + Quick Actions */}
        <Col xs={24} lg={8}>
          {/* Status Card */}
          <Card title="AGV Status" size="small" style={{ marginBottom: 16 }}>
            <Descriptions column={1} size="small">
              <Descriptions.Item label="Status">
                <Tag color={AGV_STATUS_COLORS[agv.status]}>{agv.status}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="Connected">
                <Tag color={agv.isConnected ? 'green' : 'red'}>{agv.isConnected ? 'YES' : 'NO'}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="Running">
                <Tag color={agv.isRunning ? 'green' : 'default'}>{agv.isRunning ? 'YES' : 'NO'}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="Position">
                X: {agv.posX?.toFixed(2)}, Y: {agv.posY?.toFixed(2)}, Theta: {agv.posTheta?.toFixed(2)}
              </Descriptions.Item>
              <Descriptions.Item label="Battery">
                <Progress percent={Math.round(agv.batteryLevel)} size="small" strokeColor={batteryColor} style={{ width: 120 }} />
              </Descriptions.Item>
              <Descriptions.Item label="Charging">
                <Tag color={agv.isCharging ? 'blue' : 'default'}>{agv.isCharging ? 'YES' : 'NO'}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="Speed">
                {planarSpeed.toFixed(2)} m/s
                <Text type="secondary"> ({(agv.speedX ?? 0).toFixed(2)}, {(agv.speedY ?? 0).toFixed(2)})</Text>
              </Descriptions.Item>
              <Descriptions.Item label="Load">
                <Tag color={hasLoad ? 'orange' : 'default'}>{hasLoad ? 'LOADED' : 'EMPTY'}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="Errors">
                {agv.errorCount > 0 ? <Badge count={agv.errorCount} /> : <Text type="secondary">None</Text>}
              </Descriptions.Item>
            </Descriptions>
          </Card>

          {/* Lifecycle */}
          <Card title="Lifecycle" size="small" style={{ marginBottom: 16 }}>
            <Space wrap>
              <Button
                type="primary"
                icon={<PlayCircleOutlined />}
                loading={loading === 'start'}
                onClick={() => run('start', () => startAgv(id))}
              >
                Start
              </Button>
              <Button
                danger
                icon={<StopOutlined />}
                loading={loading === 'stop'}
                onClick={() => run('stop', () => stopAgv(id))}
              >
                Stop
              </Button>
              <Button
                loading={loading === 'disc'}
                onClick={() => run('disc', () => disconnect(id))}
              >
                Disconnect
              </Button>
            </Space>
          </Card>

          {/* Load Control */}
          <Card title="Load Control" size="small" style={{ marginBottom: 16 }}>
            <Space>
              <Button
                icon={<VerticalAlignTopOutlined />}
                loading={loading === 'lift'}
                onClick={() => run('lift', () => liftAgv(id))}
                style={{ background: '#177ddc', borderColor: '#177ddc', color: '#fff' }}
              >
                Lift
              </Button>
              <Button
                icon={<VerticalAlignBottomOutlined />}
                loading={loading === 'lower'}
                onClick={() => run('lower', () => lowerAgv(id))}
              >
                Lower
              </Button>
            </Space>
          </Card>
        </Col>

        {/* Middle column: Joystick + Battery/Speed */}
        <Col xs={24} lg={8}>
          {/* Joystick */}
          <Card title="Movement Joystick" size="small" style={{ marginBottom: 16 }}>
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 16 }}>
              <VirtualJoystick
                onMove={handleJoystickMove}
                onEnd={handleJoystickEnd}
                disabled={!agv.isConnected}
              />
              <Tag color={driveTagColor}>Manual Drive: {driveDirection}</Tag>
              <Text type="secondary" style={{ textAlign: 'center' }}>
                Push up to go forward, pull down to reverse, and move left or right to steer without auto-rotating the body toward the travel vector.
              </Text>
              <div style={{ width: '100%' }}>
                <Text type="secondary">Drive Speed: {moveSpeed.toFixed(1)} m/s</Text>
                <Slider
                  min={0.1}
                  max={5}
                  step={0.1}
                  value={moveSpeed}
                  onChange={setMoveSpeed}
                />
              </div>
              <div style={{ width: '100%' }}>
                <Text type="secondary">Theta: {(agv.posTheta * 180 / Math.PI).toFixed(1)}deg ({agv.posTheta?.toFixed(2)} rad)</Text>
                <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
                  <Button
                    style={{ flex: 1 }}
                    onClick={() => {
                      const newTheta = posRef.current.theta - Math.PI / 4;
                      posRef.current.theta = newTheta;
                      apiClient.post(ENDPOINTS.control.position(id), {
                        x: posRef.current.x, y: posRef.current.y, theta: newTheta, mapId: '',
                      }).catch(() => {});
                    }}
                    disabled={!agv.isConnected}
                  >
                    Rotate Left -45deg
                  </Button>
                  <Button
                    style={{ flex: 1 }}
                    onClick={() => {
                      const newTheta = posRef.current.theta + Math.PI / 4;
                      posRef.current.theta = newTheta;
                      apiClient.post(ENDPOINTS.control.position(id), {
                        x: posRef.current.x, y: posRef.current.y, theta: newTheta, mapId: '',
                      }).catch(() => {});
                    }}
                    disabled={!agv.isConnected}
                  >
                    Rotate Right +45deg
                  </Button>
                </div>
              </div>
            </div>
          </Card>

          {/* Battery Control */}
          <Card title="Battery Control" size="small" style={{ marginBottom: 16 }}>
            <Form layout="inline" initialValues={{ batteryLevel: agv.batteryLevel, charging: agv.isCharging }}
              onFinish={(v) => run('bat', () => setBattery(id, v.batteryLevel, v.charging))}>
              <Form.Item name="batteryLevel" label="Level %">
                <InputNumber min={0} max={100} style={{ width: 80 }} />
              </Form.Item>
              <Form.Item name="charging" label="Charging">
                <Select style={{ width: 80 }} options={[
                  { label: 'Yes', value: true },
                  { label: 'No', value: false },
                ]} />
              </Form.Item>
              <Form.Item>
                <Button htmlType="submit" type="primary" loading={loading === 'bat'}>Set</Button>
              </Form.Item>
            </Form>
          </Card>

          {/* Speed Control */}
          <Card title="Speed Control" size="small" style={{ marginBottom: 16 }}>
            <Form layout="inline" initialValues={{ speed: planarSpeed }}
              onFinish={(v) => run('spd', () => setSpeed(id, v.speed))}>
              <Form.Item name="speed" label="Speed m/s">
                <InputNumber min={0} max={10} step={0.1} style={{ width: 80 }} />
              </Form.Item>
              <Form.Item>
                <Button htmlType="submit" type="primary" loading={loading === 'spd'}>Set</Button>
              </Form.Item>
            </Form>
          </Card>
        </Col>

        {/* Right column: Error Injection */}
        <Col xs={24} lg={8}>
          {/* Error Templates */}
          <Card title="Error Injection (Templates)" size="small" style={{ marginBottom: 16 }}>
            <Space wrap>
              {ERROR_TEMPLATES.map((t) => (
                <Button
                  key={t.name}
                  size="small"
                  icon={<WarningOutlined />}
                  style={{ borderColor: t.color, color: t.color }}
                  loading={loading === `tpl_${t.name}`}
                  onClick={() => run(`tpl_${t.name}`, () => injectTemplate(id, t.name))}
                >
                  {t.label}
                </Button>
              ))}
            </Space>
          </Card>

          {/* Custom Error */}
          <Card title="Custom Error" size="small" style={{ marginBottom: 16 }}>
            <Form form={errForm} layout="vertical"
              onFinish={(v) => run('err', () => addError(id, v.errorType, v.errorDescription, v.errorLevel))}>
              <Form.Item name="errorType" label="Error Type" rules={[{ required: true }]}>
                <Input placeholder="e.g. EMERGENCY_STOP" />
              </Form.Item>
              <Form.Item name="errorDescription" label="Description" rules={[{ required: true }]}>
                <Input placeholder="Description..." />
              </Form.Item>
              <Form.Item name="errorLevel" label="Level" initialValue="WARNING">
                <Select options={[
                  { label: 'WARNING', value: 'WARNING' },
                  { label: 'FATAL', value: 'FATAL' },
                ]} />
              </Form.Item>
              <Space>
                <Button htmlType="submit" type="primary" icon={<ThunderboltOutlined />} loading={loading === 'err'}>
                  Inject Error
                </Button>
                <Button danger icon={<DeleteOutlined />} loading={loading === 'clr'}
                  onClick={() => run('clr', () => clearErrors(id))}>
                  Clear All Errors
                </Button>
              </Space>
            </Form>
          </Card>

          {/* Active Errors */}
          {agv.hasErrors && (
            <Card title={`Active Errors (${agv.errorCount})`} size="small">
              {agv.errors?.map((e, i) => (
                <Alert
                  key={i}
                  type={e.errorLevel === 'FATAL' ? 'error' : 'warning'}
                  message={e.errorType}
                  description={e.description}
                  style={{ marginBottom: 8 }}
                  showIcon
                />
              ))}
            </Card>
          )}
        </Col>
      </Row>
    </div>
  );
};
