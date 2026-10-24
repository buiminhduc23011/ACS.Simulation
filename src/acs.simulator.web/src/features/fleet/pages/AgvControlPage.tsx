import React, { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import {
  Button, Space, Tag, Slider, InputNumber, Form, Input, Select,
  Row, Col, Typography, Alert, Descriptions, Badge, Switch,
  Empty, Tooltip, Flex, Tabs, Modal, Divider,
} from 'antd';
import {
  ArrowLeftOutlined, PlayCircleOutlined, StopOutlined,
  VerticalAlignTopOutlined, VerticalAlignBottomOutlined,
  ThunderboltOutlined, DeleteOutlined, WarningOutlined,
  ReloadOutlined, ApiOutlined, EnvironmentOutlined,
} from '@ant-design/icons';
import { useShallow } from 'zustand/react/shallow';
import { message } from 'antd';
import { useFleetStore } from '../../../store/fleetStore';
import { useLayoutStore } from '../../../store/layoutStore';
import { useFleetApi } from '../hooks/useFleetApi';
import type { IInboundMqttMessage, ISimMapNode } from '../../../types/agv';
import { VirtualJoystick } from '../components/VirtualJoystick';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { SectionCard } from '../../../shared/ui/SectionCard';
import { StatusBadge } from '../../../shared/ui/StatusBadge';

const { Text, Paragraph } = Typography;

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

// Stat nhỏ gọn nhúng trong global header
const HeaderStat: React.FC<{ label: string; value: React.ReactNode; color?: string }> = ({ label, value, color }) => (
  <div style={{ textAlign: 'right', lineHeight: 1.15 }}>
    <div style={{ fontSize: 9, fontWeight: 600, letterSpacing: '0.06em', color: 'var(--text-tertiary)' }}>{label}</div>
    <div style={{ fontSize: 13, fontWeight: 600, color: color ?? 'var(--text-main)' }}>{value}</div>
  </div>
);

export const AgvControlPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const agv = useFleetStore(useShallow((s) => (id ? s.agvs[id] : undefined)));
  const {
    fetchFleet, startAgv, stopAgv, setBattery, setSpeed, setOperatingMode,
    fetchInboundMessages, fetchOutboundState, fetchMapNodes, addError, clearErrors, injectTemplate,
    liftAgv, lowerAgv, clearLoads, disconnect, clearOrderState, setPosition, setActionHold,
  } = useFleetApi();

  const [loading, setLoading] = useState<string | null>(null);
  const [moveSpeed, setMoveSpeed] = useState(1.0);
  const [driveDirection, setDriveDirection] = useState<'IDLE' | 'FORWARD' | 'REVERSE' | 'TURNING'>('IDLE');
  const [errForm] = Form.useForm();
  const [inboundMessages, setInboundMessages] = useState<IInboundMqttMessage[]>([]);
  const [outboundState, setOutboundState] = useState<Record<string, unknown> | null>(null);
  const [modalMessage, setModalMessage] = useState<IInboundMqttMessage | null>(null);
  const [mapNodes, setMapNodes] = useState<{ mapId: string; mapName: string; nodes: ISimMapNode[] }[]>([]);
  const [mapNodesFailed, setMapNodesFailed] = useState(false);
  const [teleportMapId, setTeleportMapId] = useState<string | undefined>(undefined);
  const [teleportNodeId, setTeleportNodeId] = useState<string | undefined>(undefined);

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

  // Poll MQTT monitor: inbound payloads ACS gửi + outbound state AGV publish
  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    const load = async () => {
      try {
        const [msgs, state] = await Promise.all([fetchInboundMessages(id), fetchOutboundState(id)]);
        if (cancelled) return;
        setInboundMessages(msgs ?? []);
        setOutboundState(state);
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
  }, [id, fetchInboundMessages, fetchOutboundState]);

  // Load toàn bộ node của các map graph sim đang nắm; chọn map trước rồi chọn node trong map đó.
  useEffect(() => {
    let cancelled = false;
    fetchMapNodes()
      .then((maps) => {
        if (cancelled) return;
        setMapNodes(maps ?? []);
        const preferred = maps?.find((m) => m.mapId === agv?.mapId)?.mapId ?? maps?.[0]?.mapId;
        setTeleportMapId(preferred);
      })
      .catch(() => { if (!cancelled) setMapNodesFailed(true); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, fetchMapNodes]);

  const handleTeleport = useCallback(async () => {
    if (!id || !teleportMapId || !teleportNodeId) return;
    const node = mapNodes.find((m) => m.mapId === teleportMapId)?.nodes.find((n) => n.nodeId === teleportNodeId);
    if (!node) return;
    setLoading('teleport');
    try {
      await setPosition(id, node.x, node.y, posRef.current.theta, teleportMapId);
      posRef.current = { ...posRef.current, x: node.x, y: node.y, mapId: teleportMapId };
      message.success(`${id} moved to node ${node.nodeId}`);
    } catch {
      /* interceptor handles toast */
    } finally {
      setLoading(null);
    }
  }, [id, teleportMapId, teleportNodeId, mapNodes, setPosition]);

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

  const postPosition = useCallback((theta: number) => {
    if (!id) return;
    apiClient.post(ENDPOINTS.control.position(id), {
      x: posRef.current.x, y: posRef.current.y, theta, mapId: '',
    }).catch(() => {});
  }, [id]);

  const isManual = agv ? (agv.operatingMode ?? 'AUTOMATIC').toUpperCase() === 'MANUAL' : false;

  // Đưa info + lifecycle actions của AGV lên global header (giống Fleet page)
  const setHeaderLeft = useLayoutStore((s) => s.setHeaderLeft);
  const setHeaderRight = useLayoutStore((s) => s.setHeaderRight);
  useEffect(() => {
    if (!agv || !id) { setHeaderLeft(null); setHeaderRight(null); return; }
    const batColor = (agv.batteryLevel ?? 0) > 50 ? 'var(--success-color)' : (agv.batteryLevel ?? 0) > 20 ? 'var(--warning-color)' : 'var(--error-color)';
    const notif = agv.activeNotification;
    const notifColor = notif?.type === 'ERROR' ? 'var(--error-color)' : notif?.type === 'WARNING' ? 'var(--warning-color)' : 'var(--info-color)';
    setHeaderLeft(
      <Flex align="center" gap={10}>
        <Button type="text" size="small" icon={<ArrowLeftOutlined />} onClick={() => navigate('/fleet')} />
        <Text strong style={{ fontSize: 14 }}>{agv.serialNumber}</Text>
        <Text type="secondary" style={{ fontSize: 11, whiteSpace: 'nowrap' }}>
          {agv.manufacturer || 'N/A'} · {agv.ipAddress} · map {agv.mapId || '—'} · pos {agv.posX?.toFixed(1)}, {agv.posY?.toFixed(1)}
        </Text>
        <StatusBadge variant={agv.isConnected ? 'success' : 'error'} text={agv.isConnected ? 'ONLINE' : 'OFFLINE'} />
        <StatusBadge status={agv.status} />
        <StatusBadge variant={isManual ? 'warning' : 'neutral'} text={agv.operatingMode ?? 'AUTOMATIC'} dot={false} />
        {agv.holdActions && <StatusBadge variant="warning" text="HOLD" dot={false} />}
        {notif && (
          <Tooltip title={`SCREEN ${notif.type}: ${notif.message}`}>
            <Text ellipsis style={{ fontSize: 12, fontWeight: 600, color: notifColor, maxWidth: 280 }}>
              ⚠ {notif.message}
            </Text>
          </Tooltip>
        )}
      </Flex>
    );
    setHeaderRight(
      <Flex align="center" gap={14}>
        <HeaderStat label="Battery" value={`${Math.round(agv.batteryLevel ?? 0)}%${agv.isCharging ? ' ⚡' : ''}`} color={batColor} />
        <HeaderStat label="Speed" value={`${Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0).toFixed(2)} m/s`} />
        <HeaderStat label="Load" value={agv.loadCount ?? 0} />
        <HeaderStat label="Errors" value={agv.errorCount ?? 0} color={agv.errorCount > 0 ? 'var(--error-color)' : undefined} />
        <Button size="small" type="primary" icon={<PlayCircleOutlined />} disabled={agv.isRunning}
          loading={loading === 'start'} onClick={() => run('start', () => startAgv(id))}>Start</Button>
        <Button size="small" danger icon={<StopOutlined />} disabled={!agv.isRunning}
          loading={loading === 'stop'} onClick={() => run('stop', () => stopAgv(id))}>Stop</Button>
      </Flex>
    );
    return () => { setHeaderLeft(null); setHeaderRight(null); };
    // run/startAgv/stopAgv là closure ổn định, chỉ deps dữ liệu hiển thị
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agv, id, loading, isManual, navigate, setHeaderLeft, setHeaderRight]);

  if (!id) return <div>No AGV ID specified</div>;
  if (!agv) {
    return (
      <div>
        <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/fleet')}>Back to Fleet</Button>
        <Alert type="warning" message={`AGV "${id}" not found. It may not have been created yet.`} style={{ marginTop: 16 }} />
      </div>
    );
  }

  const planarSpeed = Math.hypot(agv.speedX ?? 0, agv.speedY ?? 0);
  const driveTagColor =
    driveDirection === 'FORWARD' ? 'green' :
    driveDirection === 'REVERSE' ? 'orange' :
    driveDirection === 'TURNING' ? 'blue' :
    'default';
  const orderId = agv.orderId || '(none)';
  const modalSafetyActions = modalMessage?.topicType === 'order'
    ? extractDebugSafetyActions(modalMessage.payload)
    : [];

  return (
    <div>
      <Row gutter={[16, 16]}>
        {/* Left: status + lifecycle + operating mode */}
        <Col xs={24} lg={8}>
          <SectionCard title="Vehicle Control" style={{ marginBottom: 16 }}>
            <Flex vertical gap={10}>
              {/* Mode + mission state */}
              <Flex align="center" justify="space-between" gap={8}>
                <Flex align="center" gap={8}>
                  <Text className="stat-title" style={{ width: 52 }}>Mode</Text>
                  <StatusBadge variant={isManual ? 'warning' : 'info'} text={agv.operatingMode ?? 'AUTOMATIC'} />
                </Flex>
                <Space size={6}>
                  <Text type="secondary" style={{ fontSize: 11 }}>AUTO</Text>
                  <Switch
                    size="small"
                    checked={isManual}
                    loading={loading === 'mode'}
                    onChange={(checked) =>
                      run('mode', () => setOperatingMode(id, checked ? 'MANUAL' : 'AUTOMATIC'))
                    }
                  />
                  <Text type="secondary" style={{ fontSize: 11 }}>MANUAL</Text>
                </Space>
              </Flex>
              <Flex align="center" gap={8} wrap>
                <Text className="stat-title" style={{ width: 52 }}>Order</Text>
                <Text code style={{ fontSize: 11 }}>{orderId}</Text>
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
                <Text type="secondary" style={{ fontSize: 11 }}>upd {agv.orderUpdateId ?? 0}</Text>
              </Flex>
              <Flex align="center" justify="space-between" gap={8}>
                <Flex align="center" gap={8}>
                  <Text className="stat-title" style={{ width: 52 }}>Hold</Text>
                  <Text type="secondary" style={{ fontSize: 11 }}>Keep PICK / DROP / WAIT at WAITING</Text>
                </Flex>
                <Switch
                  size="small"
                  checked={agv.holdActions === true}
                  loading={loading === 'action-hold'}
                  onChange={(checked) => run('action-hold', () => setActionHold(id, checked))}
                />
              </Flex>

              <Divider style={{ margin: '4px 0' }} />

              {/* Connection + load */}
              <Flex align="center" gap={8} wrap>
                <Text className="stat-title" style={{ width: 52 }}>Load</Text>
                <Space wrap size={6}>
                  <Tooltip title="Force-disconnect MQTT, auto-reconnect after 30s">
                    <Button size="small" loading={loading === 'disc'} onClick={() => run('disc', () => disconnect(id))}>
                      Disconnect 30s
                    </Button>
                  </Tooltip>
                  <Button
                    size="small"
                    icon={<VerticalAlignTopOutlined />}
                    loading={loading === 'lift'}
                    onClick={() => run('lift', () => liftAgv(id))}
                  >
                    Lift
                  </Button>
                  <Button
                    size="small"
                    icon={<VerticalAlignBottomOutlined />}
                    loading={loading === 'lower'}
                    onClick={() => run('lower', () => lowerAgv(id))}
                  >
                    Lower
                  </Button>
                  <Button
                    size="small"
                    danger
                    icon={<DeleteOutlined />}
                    loading={loading === 'clearLoads'}
                    onClick={() => run('clearLoads', () => clearLoads(id))}
                  >
                    Clear Shelf
                  </Button>
                </Space>
              </Flex>

              <Divider style={{ margin: '4px 0' }} />

              {/* Battery + speed */}
              <Flex align="center" gap={8} wrap>
                <Text className="stat-title" style={{ width: 52 }}>Battery</Text>
                <Form layout="inline" size="small" initialValues={{ batteryLevel: agv.batteryLevel, charging: agv.isCharging }}
                  onFinish={(v) => run('bat', () => setBattery(id, v.batteryLevel, v.charging))}>
                  <Form.Item name="batteryLevel" style={{ marginBottom: 0 }}>
                    <InputNumber min={0} max={100} style={{ width: 110 }} addonAfter="%" />
                  </Form.Item>
                  <Form.Item name="charging" style={{ marginBottom: 0 }}>
                    <Select size="small" style={{ width: 116 }} options={[
                      { label: '⚡ Charging', value: true },
                      { label: 'Not charging', value: false },
                    ]} />
                  </Form.Item>
                  <Button htmlType="submit" size="small" type="primary" loading={loading === 'bat'}>Set</Button>
                </Form>
              </Flex>
              <Flex align="center" gap={8} wrap>
                <Text className="stat-title" style={{ width: 52 }}>Speed</Text>
                <Form layout="inline" size="small" initialValues={{ speed: planarSpeed }}
                  onFinish={(v) => run('spd', () => setSpeed(id, v.speed))}>
                  <Form.Item name="speed" style={{ marginBottom: 0 }}>
                    <InputNumber min={0} max={10} step={0.1} style={{ width: 110 }} addonAfter="m/s" />
                  </Form.Item>
                  <Button htmlType="submit" size="small" type="primary" loading={loading === 'spd'}>Set</Button>
                </Form>
              </Flex>
            </Flex>
          </SectionCard>
        </Col>

        {/* Middle: joystick + teleport */}
        <Col xs={24} lg={8}>
          <SectionCard title="Movement Joystick" style={{ marginBottom: 16 }}>
            <Flex vertical align="center" gap={16}>
              <VirtualJoystick
                onMove={handleJoystickMove}
                onEnd={handleJoystickEnd}
                disabled={!agv.isConnected}
              />
              <Tag color={driveTagColor}>Manual Drive: {driveDirection}</Tag>
              <Text type="secondary" style={{ textAlign: 'center', fontSize: 12 }}>
                Push up to go forward, pull down to reverse, and move left or right to steer without auto-rotating the body toward the travel vector.
              </Text>
              <div style={{ width: '100%' }}>
                <Text type="secondary" style={{ fontSize: 12 }}>Drive Speed: {moveSpeed.toFixed(1)} m/s</Text>
                <Slider min={0.1} max={5} step={0.1} value={moveSpeed} onChange={setMoveSpeed} />
              </div>
              <div style={{ width: '100%' }}>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  Theta: {(agv.posTheta * 180 / Math.PI).toFixed(1)}deg ({agv.posTheta?.toFixed(2)} rad)
                </Text>
                <Flex gap={8} style={{ marginTop: 8 }}>
                  <Button
                    style={{ flex: 1 }}
                    onClick={() => {
                      const newTheta = posRef.current.theta - Math.PI / 4;
                      posRef.current.theta = newTheta;
                      postPosition(newTheta);
                    }}
                    disabled={!agv.isConnected}
                  >
                    Rotate Left -45°
                  </Button>
                  <Button
                    style={{ flex: 1 }}
                    onClick={() => {
                      const newTheta = posRef.current.theta + Math.PI / 4;
                      posRef.current.theta = newTheta;
                      postPosition(newTheta);
                    }}
                    disabled={!agv.isConnected}
                  >
                    Rotate Right +45°
                  </Button>
                </Flex>
              </div>
            </Flex>
          </SectionCard>

          <SectionCard title={<Space><EnvironmentOutlined /> Set Position on Node</Space>} style={{ marginBottom: 16 }}>
            {mapNodesFailed ? (
              <Alert type="warning" showIcon message="No map graph loaded in simulator" />
            ) : (
              <Space direction="vertical" style={{ width: '100%' }}>
                <Space.Compact style={{ width: '100%' }}>
                  <Select
                    showSearch
                    placeholder="Map"
                    style={{ width: '45%' }}
                    value={teleportMapId}
                    onChange={(v) => { setTeleportMapId(v); setTeleportNodeId(undefined); }}
                    optionFilterProp="label"
                    options={mapNodes.map((m) => ({
                      value: m.mapId,
                      label: `${m.mapName} (${m.mapId})`,
                    }))}
                  />
                  <Select
                    showSearch
                    allowClear
                    placeholder="Node / tag..."
                    style={{ width: '55%' }}
                    value={teleportNodeId}
                    onChange={setTeleportNodeId}
                    optionFilterProp="label"
                    disabled={!teleportMapId}
                    options={(mapNodes.find((m) => m.mapId === teleportMapId)?.nodes ?? []).map((n) => ({
                      value: n.nodeId,
                      label: n.rfidTag ? `${n.nodeId} · tag:${n.rfidTag}` : n.nodeId,
                    }))}
                    notFoundContent="No nodes on this map"
                  />
                  <Button type="primary" disabled={!teleportNodeId} loading={loading === 'teleport'} onClick={handleTeleport}>
                    Set
                  </Button>
                </Space.Compact>
                {(() => {
                  const node = mapNodes.find((m) => m.mapId === teleportMapId)?.nodes.find((n) => n.nodeId === teleportNodeId);
                  if (node && (agv.navigationTechnologyId ?? 1) === 3 && !node.rfidTag) {
                    return (
                      <Alert
                        type="warning" showIcon
                        message={`Node ${node.nodeId} has no RFID tag — line AGV will raise missingRfidTag FATAL`}
                      />
                    );
                  }
                  return (
                    <Text type="secondary" style={{ fontSize: 12 }}>
                      Teleports onto the node position — line-guided AGVs report the node RFID tag as lastNodeId.
                    </Text>
                  );
                })()}
              </Space>
            )}
          </SectionCard>
        </Col>

        {/* Right: errors + MQTT debug */}
        <Col xs={24} lg={8}>
          <SectionCard title="Error Injection" style={{ marginBottom: 16 }}>
            <Select
              placeholder="Inject template error..."
              style={{ width: '100%', marginBottom: 12 }}
              loading={loading === 'tpl'}
              value={null as unknown as string}
              onChange={(v) => run('tpl', () => injectTemplate(id, v))}
              options={ERROR_TEMPLATES.map((t) => ({
                value: t.name,
                label: (
                  <Space size={6}>
                    <WarningOutlined style={{ color: t.color }} />
                    {t.label}
                  </Space>
                ),
              }))}
            />
            <Form form={errForm} layout="vertical" size="small"
              onFinish={(v) => run('err', () => addError(id, v.errorType, v.errorDescription, v.errorLevel))}>
              <Flex gap={8}>
                <Form.Item name="errorType" label="Type" rules={[{ required: true }]} style={{ flex: 1, marginBottom: 8 }}>
                  <Input placeholder="e.g. EMERGENCY_STOP" />
                </Form.Item>
                <Form.Item name="errorLevel" label="Level" initialValue="WARNING" style={{ width: 120, marginBottom: 8 }}>
                  <Select options={[
                    { label: 'WARNING', value: 'WARNING' },
                    { label: 'FATAL', value: 'FATAL' },
                  ]} />
                </Form.Item>
              </Flex>
              <Form.Item name="errorDescription" label="Description" rules={[{ required: true }]} style={{ marginBottom: 8 }}>
                <Input placeholder="Description..." />
              </Form.Item>
              <Space>
                <Button htmlType="submit" type="primary" icon={<ThunderboltOutlined />} loading={loading === 'err'}>
                  Inject Error
                </Button>
                <Button danger icon={<DeleteOutlined />} loading={loading === 'clr'}
                  onClick={() => run('clr', () => clearErrors(id))}>
                  Clear All
                </Button>
              </Space>
            </Form>
          </SectionCard>

          {agv.hasErrors && (
            <SectionCard title={`Active Errors (${agv.errorCount})`} style={{ marginBottom: 16 }}>
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
            </SectionCard>
          )}

          <SectionCard
            title={
              <Space>
                <ApiOutlined />
                MQTT Monitor
              </Space>
            }
            style={{ marginBottom: 16 }}
            extra={
              <Button
                size="small"
                type="text"
                icon={<ReloadOutlined />}
                onClick={() => {
                  if (!id) return;
                  void fetchInboundMessages(id).then((msgs) => setInboundMessages(msgs ?? []));
                  void fetchOutboundState(id).then(setOutboundState);
                }}
              />
            }
          >
            <Tabs
              size="small"
              items={[
                {
                  key: 'inbound',
                  label: (
                    <span>
                      Inbound <Badge count={inboundMessages.length} size="small" color="var(--primary-color)" />
                    </span>
                  ),
                  children: (
                    <>
            <Paragraph type="secondary" style={{ marginBottom: 12, fontSize: 12 }}>
              Raw order / instantActions payloads ACS publishes to this AGV. Polls every {INBOUND_POLL_MS / 1000}s — click a message to view its JSON payload.
            </Paragraph>
            {inboundMessages.length === 0 ? (
              <Empty description="No order or instantActions received yet" image={Empty.PRESENTED_IMAGE_SIMPLE} />
            ) : (
              <div style={{ maxHeight: 360, overflowY: 'auto' }}>
                {inboundMessages.map((msg, index) => (
                  <div
                    key={`${msg.timestamp}-${index}`}
                    onClick={() => setModalMessage(msg)}
                    style={{
                      padding: '8px 10px',
                      marginBottom: 6,
                      borderRadius: 6,
                      cursor: 'pointer',
                      border: '1px solid var(--border-color)',
                    }}
                  >
                    <Space wrap size={4}>
                      <StatusBadge
                        variant={msg.topicType === 'order' ? 'info' : msg.topicType === 'notifications' ? 'warning' : 'neutral'}
                        text={msg.topicType}
                        dot={false}
                      />
                      <StatusBadge
                        variant={msg.accepted ? 'success' : 'error'}
                        text={msg.accepted ? 'ACCEPTED' : 'IGNORED'}
                        dot={false}
                      />
                      <Text type="secondary" style={{ fontSize: 11 }}>{formatTime(msg.timestamp)}</Text>
                    </Space>
                    {msg.note && (
                      <div style={{ marginTop: 4 }}>
                        <Text style={{ fontSize: 12 }}>{msg.note}</Text>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
                    </>
                  ),
                },
                {
                  key: 'outbound',
                  label: 'Outbound State',
                  children: (
                    <Flex vertical gap={8}>
                      <Paragraph type="secondary" style={{ marginBottom: 4, fontSize: 12 }}>
                        State payload this AGV publishes to{' '}
                        <Text code style={{ fontSize: 11 }}>
                          uagv/v2/{agv.manufacturer}/{agv.serialNumber}/state
                        </Text>{' '}
                        — polls every {INBOUND_POLL_MS / 1000}s.
                      </Paragraph>
                      {outboundState ? (
                        <pre style={{
                          margin: 0,
                          maxHeight: 360,
                          overflow: 'auto',
                          background: '#1e1e1e',
                          color: '#d4d4d4',
                          padding: 12,
                          borderRadius: 6,
                          fontSize: 12,
                          lineHeight: 1.45,
                        }}>
                          {JSON.stringify(outboundState, null, 2)}
                        </pre>
                      ) : (
                        <Empty description="No state published yet" image={Empty.PRESENTED_IMAGE_SIMPLE} />
                      )}
                    </Flex>
                  ),
                },
              ]}
            />
            <Modal
              open={modalMessage !== null}
              onCancel={() => setModalMessage(null)}
              footer={null}
              width={720}
              title={
                modalMessage && (
                  <Space size={6}>
                    <StatusBadge
                      variant={modalMessage.topicType === 'order' ? 'info' : modalMessage.topicType === 'notifications' ? 'warning' : 'neutral'}
                      text={modalMessage.topicType}
                      dot={false}
                    />
                    <StatusBadge
                      variant={modalMessage.accepted ? 'success' : 'error'}
                      text={modalMessage.accepted ? 'ACCEPTED' : 'IGNORED'}
                      dot={false}
                    />
                    <Text type="secondary" style={{ fontSize: 12 }}>{formatTime(modalMessage.timestamp)}</Text>
                  </Space>
                )
              }
            >
              {modalMessage && (
                <>
                  <Descriptions size="small" column={1} style={{ marginBottom: 10 }}>
                    <Descriptions.Item label="Topic">
                      <Text code style={{ fontSize: 11 }}>{modalMessage.topic}</Text>
                    </Descriptions.Item>
                    {modalMessage.note && (
                      <Descriptions.Item label="Note">{modalMessage.note}</Descriptions.Item>
                    )}
                    {modalSafetyActions.length > 0 && (
                      <Descriptions.Item label="Safety actions">
                        <Space wrap>
                          {modalSafetyActions.map((action, index) => (
                            <Tag color="gold" key={`${action.nodeId}-${action.label}-${index}`}>
                              {action.label} @ {action.nodeId}
                            </Tag>
                          ))}
                        </Space>
                      </Descriptions.Item>
                    )}
                    <Descriptions.Item label="Time">{modalMessage.timestamp}</Descriptions.Item>
                  </Descriptions>
                  <pre style={{
                    margin: 0,
                    maxHeight: '55vh',
                    overflow: 'auto',
                    background: '#1e1e1e',
                    color: '#d4d4d4',
                    padding: 12,
                    borderRadius: 6,
                    fontSize: 12,
                    lineHeight: 1.45,
                  }}>
                    {formatJsonPayload(modalMessage.payload)}
                  </pre>
                </>
              )}
            </Modal>
          </SectionCard>
        </Col>
      </Row>
    </div>
  );
};
