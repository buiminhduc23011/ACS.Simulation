import React, { useMemo } from 'react';
import { Row, Col, Card, Statistic, Typography, Timeline, Tag, Empty } from 'antd';
import { PieChart, Pie, Cell, BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, Legend } from 'recharts';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { AGV_STATUS_COLORS } from '../../../types/agv';

const { Title } = Typography;

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

export const DashboardPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const events = useFleetStore((s) => s.events);

  const stats = useMemo(() => {
    const total = agvs.length;
    const idle = agvs.filter((a) => a.status === 'IDLE').length;
    const driving = agvs.filter((a) => a.status === 'DRIVING').length;
    const errors = agvs.filter((a) => a.status === 'ERROR').length;
    const offline = agvs.filter((a) => a.status === 'OFFLINE').length;
    const avgBattery = total ? agvs.reduce((s, a) => s + (a.batteryLevel ?? 0), 0) / total : 0;
    return { total, idle, driving, errors, offline, avgBattery };
  }, [agvs]);

  const pieData = useMemo(() => [
    { name: 'IDLE', value: stats.idle },
    { name: 'DRIVING', value: stats.driving },
    { name: 'ERROR', value: stats.errors },
    { name: 'OFFLINE', value: stats.offline },
  ].filter((d) => d.value > 0), [stats]);

  const batteryData = useMemo(() =>
    agvs.map((a) => ({ name: a.id, battery: Math.round(a.batteryLevel ?? 0) }))
      .sort((a, b) => a.battery - b.battery),
    [agvs]
  );

  return (
    <div>
      <Title level={4} style={{ marginBottom: 16 }}>Dashboard</Title>

      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Total AGVs" value={stats.total} /></Card>
        </Col>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Idle" value={stats.idle} valueStyle={{ color: '#4CAF50' }} /></Card>
        </Col>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Driving" value={stats.driving} valueStyle={{ color: '#FF9800' }} /></Card>
        </Col>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Errors" value={stats.errors} valueStyle={{ color: '#F44336' }} /></Card>
        </Col>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Offline" value={stats.offline} valueStyle={{ color: '#666' }} /></Card>
        </Col>
        <Col xs={12} sm={8} lg={4}>
          <Card><Statistic title="Avg Battery" value={stats.avgBattery.toFixed(1)} suffix="%" /></Card>
        </Col>
      </Row>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={8}>
          <Card title="Status Distribution" style={{ height: 320 }}>
            {pieData.length === 0 ? <Empty description="No AGVs" /> : (
              <ResponsiveContainer width="100%" height={240}>
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
          </Card>
        </Col>

        <Col xs={24} lg={8}>
          <Card title="Battery Levels" style={{ height: 320 }}>
            {batteryData.length === 0 ? <Empty description="No AGVs" /> : (
              <ResponsiveContainer width="100%" height={240}>
                <BarChart data={batteryData} layout="vertical" margin={{ left: 10 }}>
                  <XAxis type="number" domain={[0, 100]} unit="%" />
                  <YAxis type="category" dataKey="name" width={70} tick={{ fontSize: 11 }} />
                  <Tooltip formatter={(v) => [`${v}%`, 'Battery']} />
                  <Bar dataKey="battery" fill="#5C6BC0" radius={[0, 4, 4, 0]}>
                    {batteryData.map((entry) => (
                      <Cell key={entry.name}
                        fill={entry.battery > 50 ? '#52c41a' : entry.battery > 20 ? '#faad14' : '#ff4d4f'} />
                    ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            )}
          </Card>
        </Col>

        <Col xs={24} lg={8}>
          <Card title="Recent Events" style={{ height: 320, overflowY: 'auto' }}>
            {events.length === 0 ? <Empty description="No events" /> : (
              <Timeline
                items={events.slice(0, 20).map((e) => ({
                  key: e.timestamp + e.agvId,
                  color: e.eventType === 'ErrorAdded' ? 'red' : e.eventType === 'AgvCreated' ? 'green' : 'blue',
                  children: (
                    <div style={{ fontSize: 12 }}>
                      <Tag style={{ fontSize: 10 }}>{e.agvId}</Tag>
                      <span>{e.eventType}</span>
                      {e.message && <div style={{ color: '#888', fontSize: 11 }}>{e.message}</div>}
                    </div>
                  ),
                }))}
              />
            )}
          </Card>
        </Col>
      </Row>
    </div>
  );
};
