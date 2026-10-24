import React, { useEffect, useMemo } from 'react';
import { useNavigate } from 'react-router-dom';
import { Row, Col, Statistic, Timeline, Empty, Typography, Flex, Table, Progress, Button } from 'antd';
import {
  RobotOutlined, CheckCircleOutlined, ThunderboltOutlined,
  WarningOutlined, DisconnectOutlined, DashboardOutlined,
} from '@ant-design/icons';
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, Cell } from 'recharts';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useLayoutStore } from '../../../store/layoutStore';
import type { AgvStatus, ISimAgv } from '../../../types/agv';
import { SectionCard } from '../../../shared/ui/SectionCard';
import { StatusBadge } from '../../../shared/ui/StatusBadge';

const { Text } = Typography;

// Màu status theo semantic tokens — đồng bộ light/dark theme
const STATUS_COLOR: Record<AgvStatus, string> = {
  IDLE: 'var(--success-color)',
  DRIVING: 'var(--info-color)',
  ERROR: 'var(--error-color)',
  OFFLINE: 'var(--neutral-color)',
  CHARGING: 'var(--warning-color)',
};
const STATUS_ORDER: AgvStatus[] = ['DRIVING', 'IDLE', 'CHARGING', 'ERROR', 'OFFLINE'];

const EVENT_COLORS: Record<string, string> = {
  ErrorAdded: 'red', ErrorInjected: 'red', Deleted: 'red',
  Created: 'green', Started: 'green', ErrorsCleared: 'green',
  Stopped: 'gray', Updated: 'blue',
};

export const DashboardPage: React.FC = () => {
  const navigate = useNavigate();
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const events = useFleetStore((s) => s.events);
  const setHeaderLeft = useLayoutStore((s) => s.setHeaderLeft);

  useEffect(() => {
    setHeaderLeft(
      <div style={{ lineHeight: 1.2 }}>
        <div><Text strong style={{ fontSize: 14 }}>Dashboard</Text></div>
        <Text type="secondary" style={{ fontSize: 11 }}>Simulated fleet overview</Text>
      </div>
    );
    return () => setHeaderLeft(null);
  }, [setHeaderLeft]);

  const stats = useMemo(() => {
    const total = agvs.length;
    const online = agvs.filter((a) => a.isConnected).length;
    const driving = agvs.filter((a) => a.status === 'DRIVING').length;
    const errors = agvs.filter((a) => a.status === 'ERROR').length;
    const avgBattery = total ? agvs.reduce((s, a) => s + (a.batteryLevel ?? 0), 0) / total : 0;
    return { total, online, driving, errors, avgBattery };
  }, [agvs]);

  const statusRows = useMemo(() =>
    STATUS_ORDER.map((status) => ({
      status,
      count: agvs.filter((a) => a.status === status).length,
    })).filter((r) => r.count > 0),
    [agvs]);

  // Phân bố pin theo bucket — đọc nhanh hơn bar theo từng xe
  const batteryBuckets = useMemo(() => {
    const buckets = [
      { range: '0-20', min: 0, max: 20, color: 'var(--error-color)', count: 0 },
      { range: '21-40', min: 21, max: 40, color: 'var(--warning-color)', count: 0 },
      { range: '41-60', min: 41, max: 60, color: 'var(--info-color)', count: 0 },
      { range: '61-80', min: 61, max: 80, color: 'var(--info-color)', count: 0 },
      { range: '81-100', min: 81, max: 100, color: 'var(--success-color)', count: 0 },
    ];
    agvs.forEach((a) => {
      const b = buckets.find((x) => (a.batteryLevel ?? 0) >= x.min && (a.batteryLevel ?? 0) <= x.max);
      if (b) b.count += 1;
    });
    return buckets;
  }, [agvs]);

  const lowBatteryAgvs = useMemo(() =>
    [...agvs].sort((a, b) => (a.batteryLevel ?? 0) - (b.batteryLevel ?? 0)).slice(0, 5),
    [agvs]);

  const missionAgvs = useMemo(() =>
    agvs.filter((a) => a.orderId).sort((a, b) => a.id.localeCompare(b.id)),
    [agvs]);

  const statCards = [
    { title: 'Total AGVs', value: stats.total, icon: <RobotOutlined />, color: 'var(--primary-color)' },
    { title: 'Online', value: stats.online, icon: <CheckCircleOutlined />, color: 'var(--success-color)' },
    { title: 'Driving', value: stats.driving, icon: <ThunderboltOutlined />, color: 'var(--info-color)' },
    { title: 'Errors', value: stats.errors, icon: <WarningOutlined />, color: 'var(--error-color)' },
    { title: 'Avg Battery', value: `${stats.avgBattery.toFixed(0)}%`, icon: <DashboardOutlined />, color: 'var(--warning-color)' },
  ];

  return (
    <div>
      <Flex gap={16} wrap style={{ marginBottom: 16 }}>
        {statCards.map((s) => (
          <SectionCard key={s.title} uiVariant="compact" style={{ flex: '1 1 160px' }}>
            <Statistic
              title={<Text className="stat-title">{s.title}</Text>}
              value={s.value}
              prefix={<span style={{ color: s.color, marginRight: 4 }}>{s.icon}</span>}
              valueStyle={{ fontSize: 22, fontWeight: 700, color: 'var(--text-main)' }}
            />
          </SectionCard>
        ))}
      </Flex>

      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={24} lg={8}>
          <SectionCard title="Fleet Status" style={{ height: '100%' }}>
            {agvs.length === 0 ? <Empty description="No AGVs" image={Empty.PRESENTED_IMAGE_SIMPLE} /> : (
              <Flex vertical gap={14}>
                <Flex style={{ height: 10, borderRadius: 4, overflow: 'hidden', background: 'var(--neutral-bg)' }}>
                  {statusRows.map((r) => (
                    <div key={r.status} title={`${r.status}: ${r.count}`}
                      style={{ width: `${(r.count / agvs.length) * 100}%`, background: STATUS_COLOR[r.status] }} />
                  ))}
                </Flex>
                <Flex vertical gap={6}>
                  {statusRows.map((r) => (
                    <Flex key={r.status} align="center" justify="space-between">
                      <Flex align="center" gap={8}>
                        <span style={{ width: 8, height: 8, borderRadius: 2, background: STATUS_COLOR[r.status] }} />
                        <Text style={{ fontSize: 12 }}>{r.status}</Text>
                      </Flex>
                      <Text style={{ fontSize: 12 }}>
                        <Text strong>{r.count}</Text>
                        <Text type="secondary"> · {Math.round((r.count / agvs.length) * 100)}%</Text>
                      </Text>
                    </Flex>
                  ))}
                </Flex>
              </Flex>
            )}
          </SectionCard>
        </Col>

        <Col xs={24} lg={8}>
          <SectionCard title="Battery" style={{ height: '100%' }}>
            {agvs.length === 0 ? <Empty description="No AGVs" image={Empty.PRESENTED_IMAGE_SIMPLE} /> : (
              <Flex vertical gap={8}>
                <ResponsiveContainer width="100%" height={110}>
                  <BarChart data={batteryBuckets} margin={{ top: 0, right: 0, bottom: 0, left: 0 }}>
                    <XAxis dataKey="range" tick={{ fontSize: 10 }} interval={0} />
                    <YAxis hide domain={[0, 'dataMax']} />
                    <Tooltip formatter={(v) => [v, 'AGVs']} />
                    <Bar dataKey="count" radius={[3, 3, 0, 0]}>
                      {batteryBuckets.map((b) => <Cell key={b.range} fill={b.color} />)}
                    </Bar>
                  </BarChart>
                </ResponsiveContainer>
                <Flex vertical gap={4}>
                  {lowBatteryAgvs.map((a) => (
                    <Flex key={a.id} align="center" gap={8}>
                      <Text style={{ fontSize: 11, width: 84 }} ellipsis>{a.serialNumber}</Text>
                      <Progress
                        percent={Math.round(a.batteryLevel ?? 0)} size="small" style={{ flex: 1, margin: 0 }}
                        strokeColor={(a.batteryLevel ?? 0) > 50 ? 'var(--success-color)' : (a.batteryLevel ?? 0) > 20 ? 'var(--warning-color)' : 'var(--error-color)'}
                      />
                    </Flex>
                  ))}
                </Flex>
              </Flex>
            )}
          </SectionCard>
        </Col>

        <Col xs={24} lg={8}>
          <SectionCard title="Recent Events" style={{ height: '100%' }}>
            {events.length === 0 ? <Empty description="No events" image={Empty.PRESENTED_IMAGE_SIMPLE} /> : (
              <div style={{ maxHeight: 220, overflowY: 'auto' }}>
                <Timeline
                  items={events.slice(0, 30).map((e) => ({
                    key: e.timestamp + e.agvId,
                    color: EVENT_COLORS[e.eventType] ?? 'blue',
                    children: (
                      <div style={{ fontSize: 12 }}>
                        <Flex align="center" gap={6} wrap>
                          <Text strong style={{ fontSize: 12 }}>{e.agvId}</Text>
                          <StatusBadge variant="neutral" text={e.eventType} dot={false} />
                        </Flex>
                        {e.message && <div style={{ color: 'var(--text-tertiary)', fontSize: 11, marginTop: 2 }}>{e.message}</div>}
                      </div>
                    ),
                  }))}
                />
              </div>
            )}
          </SectionCard>
        </Col>
      </Row>

      <SectionCard title={`Active Missions (${missionAgvs.length})`} style={{ marginBottom: 16 }}>
        {missionAgvs.length === 0 ? (
          <Empty description="No AGV is executing an order" image={Empty.PRESENTED_IMAGE_SIMPLE} />
        ) : (
          <Table<ISimAgv>
            size="small"
            rowKey="id"
            dataSource={missionAgvs}
            pagination={false}
            scroll={{ y: 280 }}
            columns={[
              {
                title: 'AGV', width: 120,
                render: (_, a) => (
                  <Button type="link" onClick={() => navigate(`/fleet/${a.id}/control`)}
                    style={{ padding: 0, height: 'auto', fontWeight: 600, fontSize: 12, color: 'var(--text-main)' }}>
                    {a.serialNumber}
                  </Button>
                ),
              },
              {
                title: 'Order', dataIndex: 'orderId', width: 220,
                render: (v: string, a) => (
                  <Text style={{ fontSize: 12, fontFamily: 'monospace' }} ellipsis={{ tooltip: v }}>
                    {v} <Text type="secondary" style={{ fontSize: 11 }}>#{a.orderUpdateId ?? 0}</Text>
                  </Text>
                ),
              },
              { title: 'Status', width: 110, render: (_, a) => <StatusBadge status={a.status} /> },
              {
                title: 'Battery', width: 160,
                render: (_, a) => (
                  <Progress percent={Math.round(a.batteryLevel ?? 0)} size="small" style={{ width: 110, margin: 0 }}
                    strokeColor={(a.batteryLevel ?? 0) > 50 ? 'var(--success-color)' : (a.batteryLevel ?? 0) > 20 ? 'var(--warning-color)' : 'var(--error-color)'} />
                ),
              },
              {
                title: 'Position',
                render: (_, a) => (
                  <Text style={{ fontSize: 12, fontFamily: 'monospace' }}>
                    {a.posX?.toFixed(1)}, {a.posY?.toFixed(1)} · {a.mapId || '—'}
                  </Text>
                ),
              },
            ]}
          />
        )}
      </SectionCard>

      {stats.online < stats.total && stats.total > 0 && (
        <Flex align="center" gap={6}>
          <DisconnectOutlined style={{ color: 'var(--text-quaternary)', fontSize: 12 }} />
          <Text style={{ fontSize: 12, color: 'var(--text-tertiary)' }}>
            {stats.total - stats.online} AGV(s) offline — start them from the Fleet page.
          </Text>
        </Flex>
      )}
    </div>
  );
};
