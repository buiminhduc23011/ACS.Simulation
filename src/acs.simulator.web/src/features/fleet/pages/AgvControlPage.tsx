import React, { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  Card, Button, Space, Tag, Slider, InputNumber, Form, Input, Select,
  Row, Col, Typography, Progress, Alert, Descriptions, Badge, Switch,
  Collapse, Empty,
} from 'antd';
import {
  ArrowLeftOutlined, PlayCircleOutlined, StopOutlined,
  VerticalAlignTopOutlined, VerticalAlignBottomOutlined,
  ThunderboltOutlined, DeleteOutlined, WarningOutlined,
  ReloadOutlined, ControlOutlined, ApiOutlined,
} from '@ant-design/icons';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useFleetApi } from '../hooks/useFleetApi';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import type { IInboundMqttMessage } from '../../../types/agv';
import { VirtualJoystick } from '../components/VirtualJoystick';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { useLayoutStore } from '../../../store/layoutStore';

const { Title, Text, Paragraph } = Typography;

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
const INBOUND_POLL_MS = 1500;

const DEBUG_SAFETY_ACTION_LABELS: Record<string, string> = {
  'acs.disablesafetyfront': 'Front LiDAR action (debug only)',
  'acs.disablesafetyrear': 'Rear LiDAR action (debug only)',
};

function normalizeAngle(angle: number): number {
  let normalized = angle;
  while (normalized > Math.PI) normalized -= Math.PI * 2;
  while (normalized < -Math.PI) normalized += Math.PI * 2;
  return normalized;
}

function formatJsonPayload(payload: string): string {
  try {
    return JSON.stringify(JSON.parse(payload), null, 2);
  } catch {
    return payload;
  }
}

function formatTime(ts: string): string {
  try {
    return new Date(ts).toLocaleTimeString();
  } catch {
    return ts;
  }
}

function extractDebugSafetyActions(payload: string): { nodeId: string; label: string }[] {
  try {
    const order = JSON.parse(payload) as { nodes?: Array<{ nodeId?: string; actions?: Array<{ actionType?: string }> }> };
    return order.nodes?.flatMap((node) =>
      node.actions?.flatMap((action) => {
        const label = action.actionType ? DEBUG_SAFETY_ACTION_LABELS[action.actionType.toLowerCase()] : undefined;
        return label ? [{ nodeId: node.nodeId ?? '(unknown node)', label }] : [];
      }) ?? [],
    ) ?? [];
  } catch {
    return [];
  }
}

export const AgvControlPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const agv = useFleetStore(useShallow((s) => (id ? s.agvs[id] : undefined)));
  const {
    fetchFleet, startAgv, stopAgv, setBattery, setSpeed, setOperatingMode,
    fetchInboundMessages, addError, clearErrors, injectTemplate, liftAgv, lowerAgv, clearLoads, disconnect, clearOrderState,
  } = useFleetApi();

  const [loading, setLoading] = useState<string | null>(null);
  const [moveSpeed, setMoveSpeed] = useState(1.0);
  const [driveDirection, setDriveDirection] = useState<'IDLE' | 'FORWARD' | 'REVERSE' | 'TURNING'>('IDLE');
  const [errForm] = Form.useForm();
  const [inboundMessages, setInboundMessages] = useState<IInboundMqttMessage[]>([]);
  const [selectedMessageIndex, setSelectedMessageIndex] = useState(0);

  const setHeaderLeft = useLayoutStore((s) => s.setHeaderLeft);

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

  // Poll ACS inbound MQTT messages for debug panel
  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    const load = async () => {
      try {
        const msgs = await fetchInboundMessages(id);
        if (!cancelled) setInboundMessages(msgs ?? []);
      } catch {
        /* interceptor handles toast */
      }
    };
    void load();
    const interval = setInterval(load, INBOUND_POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [id, fetchInboundMessages]);

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

  const isManual = agv ? (agv.operatingMode ?? 'AUTOMATIC').toUpperCase() === 'MANUAL' : false;

  useEffect(() => {
    if (agv) {
      setHeaderLeft(
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/fleet')} type="text" />
          <Title level={4} style={{ margin: 0 }}>AGV Control: {agv.serialNumber}</Title>
          <Tag color={AGV_STATUS_COLORS[agv.status]} style={{ fontSize: 14, padding: '2px 12px' }}>{agv.status}</Tag>
          <Tag color={isManual ? 'orange' : 'blue'}>{agv.operatingMode ?? 'AUTOMATIC'}</Tag>
          {agv.hasErrors && <Badge count={agv.errorCount} style={{ marginLeft: 4 }} />}
        </div>
      );
    }
    return () => setHeaderLeft(null);
  }, [agv, isManual, navigate, setHeaderLeft]);

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
  const loadCount = agv.loadCount ?? (agv as any).loads?.length ?? 0;
  const hasLoad = loadCount > 0;
  const planarSpeed = Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0);
  const driveTagColor =
    driveDirection === 'FORWARD' ? 'green' :
    driveDirection === 'REVERSE' ? 'orange' :
    driveDirection === 'TURNING' ? 'blue' :
    'default';
  const orderId = agv.orderId || '(none)';
  const selectedMessage = inboundMessages[selectedMessageIndex] ?? inboundMessages[0];
  const selectedSafetyActions = selectedMessage?.topicType === 'order'
    ? extractDebugSafetyActions(selectedMessage.payload)
    : [];

  return (
    <div style={{ padding: '0 8px 32px 8px' }}>
      <Row gutter={[16, 16]}>
        {/* Left column: Status + Quick Actions */}
        <Col xs={24} lg={8}>
          {/* Operating Mode */}
          <Card
            title={<Space><ControlOutlined /> Operating Mode</Space>}
            size="small"
            style={{ marginBottom: 16 }}
          >
            <Space direction="vertical" style={{ width: '100%' }} size="middle">
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                <div>
                  <Text strong>Mode: </Text>
                  <Tag color={isManual ? 'orange' : 'blue'}>{agv.operatingMode ?? 'AUTOMATIC'}</Tag>
                </div>
                <Space>
                  <Text type="secondary">AUTOMATIC</Text>
                  <Switch
                    checked={isManual}
                    checkedChildren="MANUAL"
                    unCheckedChildren="AUTO"
                    loading={loading === 'mode'}
                    onChange={(checked) =>
                      run('mode', () => setOperatingMode(id, checked ? 'MANUAL' : 'AUTOMATIC'))
                    }
                  />
                  <Text type="secondary">MANUAL</Text>
                </Space>
              </div>

              <Descriptions column={1} size="small">
                <Descriptions.Item label="OrderId">
                  <Space>
                    <Text code>{orderId}</Text>
                    {orderId !== '(none)' && (
                      <Button
                        size="small"
                        type="text"
                        danger
                        icon={<DeleteOutlined />}
                        onClick={() => run('clear-order', () => clearOrderState(id))}
                        loading={loading === 'clear-order'}
                        title="Clear Order"
                      />
                    )}
                  </Space>
                </Descriptions.Item>
                <Descriptions.Item label="OrderUpdateId">
                  {agv.orderUpdateId ?? 0}
                </Descriptions.Item>
              </Descriptions>
            </Space>
          </Card>

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
                <Tag color={hasLoad ? 'orange' : 'default'}>{hasLoad ? `LOADED (${loadCount})` : 'EMPTY'}</Tag>
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
              <Button
                danger
                icon={<DeleteOutlined />}
                loading={loading === 'clearLoads'}
                onClick={() => run('clearLoads', () => clearLoads(id))}
              >
                Clear Shelf
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
            <Card title={`Active Errors (${agv.errorCount})`} size="small" style={{ marginBottom: 16 }}>
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

        {/* Right column: ACS debug */}
        <Col xs={24} lg={8}>
          {/* ACS Inbound MQTT debug panel (Right Column) */}
      <Card
        title={
          <Space>
            <ApiOutlined />
            ACS Inbound MQTT (order / instantActions)
            <Badge count={inboundMessages.length} style={{ backgroundColor: '#1677ff' }} />
          </Space>
        }
        size="small"
        style={{ marginBottom: 16 }}
        extra={
          <Button
            size="small"
            icon={<ReloadOutlined />}
            onClick={() => {
              if (!id) return;
              void fetchInboundMessages(id).then((msgs) => setInboundMessages(msgs ?? []));
            }}
          >
            Refresh
          </Button>
        }
      >
        <Paragraph type="secondary" style={{ marginBottom: 12 }}>
          Raw payloads ACS publishes to this AGV. Polls every {INBOUND_POLL_MS / 1000}s. Use this to verify order / instantAction content while debugging.
        </Paragraph>
        {inboundMessages.length === 0 ? (
          <Empty description="No order or instantActions received yet" />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div>
              <div style={{ maxHeight: 250, overflowY: 'auto' }}>
                {inboundMessages.map((msg, index) => (
                  <div
                    key={`${msg.timestamp}-${index}`}
                    onClick={() => setSelectedMessageIndex(index)}
                    style={{
                      padding: '8px 10px',
                      marginBottom: 6,
                      borderRadius: 6,
                      cursor: 'pointer',
                      border: index === selectedMessageIndex ? '1px solid #1677ff' : '1px solid rgba(128, 128, 128, 0.2)',
                      background: index === selectedMessageIndex ? 'rgba(22, 119, 255, 0.15)' : 'transparent',
                    }}
                  >
                    <Space wrap size={4}>
                      <Tag color={msg.topicType === 'order' ? 'blue' : 'purple'}>{msg.topicType}</Tag>
                      <Tag color={msg.accepted ? 'green' : 'red'}>{msg.accepted ? 'ACCEPTED' : 'IGNORED'}</Tag>
                      <Text type="secondary" style={{ fontSize: 12 }}>{formatTime(msg.timestamp)}</Text>
                    </Space>
                    {msg.note && (
                      <div style={{ marginTop: 4 }}>
                        <Text style={{ fontSize: 12 }}>{msg.note}</Text>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
            <div>
              {selectedMessage ? (
                <div>
                  <Descriptions size="small" column={1} style={{ marginBottom: 8 }}>
                    <Descriptions.Item label="Type">
                      <Tag color={selectedMessage.topicType === 'order' ? 'blue' : 'purple'}>
                        {selectedMessage.topicType}
                      </Tag>
                    </Descriptions.Item>
                    <Descriptions.Item label="Topic">
                      <Text code style={{ fontSize: 12 }}>{selectedMessage.topic}</Text>
                    </Descriptions.Item>
                    <Descriptions.Item label="Status">
                      <Tag color={selectedMessage.accepted ? 'green' : 'red'}>
                        {selectedMessage.accepted ? 'ACCEPTED' : 'IGNORED / REJECTED'}
                      </Tag>
                    </Descriptions.Item>
                    {selectedMessage.note && (
                      <Descriptions.Item label="Note">{selectedMessage.note}</Descriptions.Item>
                    )}
                    {selectedSafetyActions.length > 0 && (
                      <Descriptions.Item label="Safety actions">
                        <Space wrap>
                          {selectedSafetyActions.map((action, index) => (
                            <Tag color="gold" key={`${action.nodeId}-${action.label}-${index}`}>
                              {action.label} @ {action.nodeId}
                            </Tag>
                          ))}
                        </Space>
                      </Descriptions.Item>
                    )}
                    <Descriptions.Item label="Time">{selectedMessage.timestamp}</Descriptions.Item>
                  </Descriptions>
                  <Collapse
                    size="small"
                    defaultActiveKey={['payload']}
                    items={[{
                      key: 'payload',
                      label: 'JSON Payload',
                      children: (
                        <pre style={{
                          margin: 0,
                          maxHeight: 250,
                          overflow: 'auto',
                          background: '#1e1e1e',
                          color: '#d4d4d4',
                          padding: 12,
                          borderRadius: 6,
                          fontSize: 12,
                          lineHeight: 1.45,
                        }}>
                          {formatJsonPayload(selectedMessage.payload)}
                        </pre>
                      ),
                    }]}
                  />
                </div>
              ) : null}
            </div>
          </div>
        )}
      </Card>
        </Col>
      </Row>
    </div>
  );
};
