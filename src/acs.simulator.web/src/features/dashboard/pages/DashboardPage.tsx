import React, { useMemo } from 'react';
import { Row, Col, Statistic, Timeline, Empty, Typography, Flex } from 'antd';
import {
  RobotOutlined, CheckCircleOutlined, ThunderboltOutlined,
  WarningOutlined, DisconnectOutlined, DashboardOutlined,
} from '@ant-design/icons';
import { PieChart, Pie, Cell, BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import { PageHeader } from '../../../shared/ui/PageHeader';
import { SectionCard } from '../../../shared/ui/SectionCard';
import { StatusBadge } from '../../../shared/ui/StatusBadge';

const { Text } = Typography;

const RADIAN = Math.PI / 180;
const renderCustomLabel = ({ cx, cy, midAngle, innerRadius, outerRadius, percent, name }: Record<string, number> & { name: string }) => {
  const radius = innerRadius + (outerRadius - innerRadius) * 0.5;
  const x = cx + radius * Math.cos(-midAngle * RADIAN);
  const y = cy + radius * Math.sin(-midAngle * RADIAN);
  return percent > 0.05 ? (
    <text x={x} y={y} fill="white" textAnchor="middle" dominantBaseline="central" fontSize={12}>
      {name}
    </text>
  ) : null;
};

const EVENT_COLORS: Record<string, string> = {
  ErrorAdded: 'red', ErrorInjected: 'red', Deleted: 'red',
  Created: 'green', Started: 'green', ErrorsCleared: 'green',
  Stopped: 'gray', Updated: 'blue',
};

export const DashboardPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const events = useFleetStore((s) => s.events);

  const stats = useMemo(() => {
    const total = agvs.length;
    const online = agvs.filter((a) => a.isConnected).length;
    const driving = agvs.filter((a) => a.status === 'DRIVING').length;
    const errors = agvs.filter((a) => a.status === 'ERROR').length;
    const avgBattery = total ? agvs.reduce((s, a) => s + (a.batteryLevel ?? 0), 0) / total : 0;
    return { total, online, driving, errors, avgBattery };
  }, [agvs]);

  const pieData = useMemo(() => [
    { name: 'IDLE', value: agvs.filter((a) => a.status === 'IDLE').length },
    { name: 'DRIVING', value: stats.driving },
    { name: 'ERROR', value: stats.errors },
    { name: 'OFFLINE', value: agvs.filter((a) => a.status === 'OFFLINE').length },
    { name: 'CHARGING', value: agvs.filter((a) => a.status === 'CHARGING').length },
  ].filter((d) => d.value > 0), [agvs, stats]);

  const batteryData = useMemo(() =>
    agvs.map((a) => ({ name: a.serialNumber, battery: Math.round(a.batteryLevel ?? 0) }))
      .sort((a, b) => a.battery - b.battery),
    [agvs]
  );

  const statCards = [
    { title: 'Total AGVs', value: stats.total, icon: <RobotOutlined />, color: 'var(--primary-color)' },
    { title: 'Online', value: stats.online, icon: <CheckCircleOutlined />, color: 'var(--success-color)' },
    { title: 'Driving', value: stats.driving, icon: <ThunderboltOutlined />, color: 'var(--info-color)' },
    { title: 'Errors', value: stats.errors, icon: <WarningOutlined />, color: 'var(--error-color)' },
    { title: 'Avg Battery', value: `${stats.avgBattery.toFixed(0)}%`, icon: <DashboardOutlined />, color: 'var(--warning-color)' },
  ];

  return (
    <div>
      <PageHeader
        icon={<DashboardOutlined />}
        title="Dashboard"
        subtitle="Simulated fleet overview"
      />

      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        {statCards.map((s) => (
          <Col key={s.title} xs={12} sm={8} lg={4} flex="1">
            <SectionCard uiVariant="compact">
              <Statistic
                title={<Text className="stat-title">{s.title}</Text>}
                value={s.value}
                prefix={<span style={{ color: s.color, marginRight: 4 }}>{s.icon}</span>}
                valueStyle={{ fontSize: 22, fontWeight: 700, color: 'var(--text-main)' }}
              />
            </SectionCard>
          </Col>
        ))}
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={8}>
          <SectionCard title="Status Distribution" style={{ height: 340 }}>
            {pieData.length === 0 ? <Empty description="No AGVs" style={{ marginTop: 60 }} /> : (
              <ResponsiveContainer width="100%" height={260}>
                <PieChart>
                  <Pie data={pieData} dataKey="value" cx="50%" cy="50%" outerRadius={90}
                    labelLine={false} label={renderCustomLabel as never}>
                    {pieData.map((entry) => (
                      <Cell key={entry.name} fill={AGV_STATUS_COLORS[entry.name as keyof typeof AGV_STATUS_COLORS]} />
                    ))}
                  </Pie>
                  <Tooltip />
                  <Legend />
                </PieChart>
              </ResponsiveContainer>
            )}
          </SectionCard>
        </Col>

        <Col xs={24} lg={8}>
          <SectionCard title="Battery Levels" style={{ height: 340 }}>
            {batteryData.length === 0 ? <Empty description="No AGVs" style={{ marginTop: 60 }} /> : (
              <ResponsiveContainer width="100%" height={260}>
                <BarChart data={batteryData} layout="vertical" margin={{ left: 10 }}>
                  <XAxis type="number" domain={[0, 100]} unit="%" />
                  <YAxis type="category" dataKey="name" width={80} tick={{ fontSize: 11 }} />
                  <Tooltip formatter={(v) => [`${v}%`, 'Battery']} />
                  <Bar dataKey="battery" fill="#5C6BC0" radius={[0, 4, 4, 0]}>
                    {batteryData.map((entry) => (
                      <Cell key={entry.name}
                        fill={entry.battery > 50 ? '#16a34a' : entry.battery > 20 ? '#d97706' : '#dc2626'} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            )}
          </SectionCard>
        </Col>

        <Col xs={24} lg={8}>
          <SectionCard title="Recent Events" style={{ height: 340 }}>
            {events.length === 0 ? <Empty description="No events" style={{ marginTop: 60 }} /> : (
              <div style={{ maxHeight: 264, overflowY: 'auto' }}>
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

      {stats.online < stats.total && stats.total > 0 && (
        <Flex align="center" gap={6} style={{ marginTop: 12 }}>
          <DisconnectOutlined style={{ color: 'var(--text-quaternary)', fontSize: 12 }} />
          <Text style={{ fontSize: 12, color: 'var(--text-tertiary)' }}>
            {stats.total - stats.online} AGV(s) offline — start them from the Fleet page.
          </Text>
        </Flex>
      )}
    </div>
  );
};
