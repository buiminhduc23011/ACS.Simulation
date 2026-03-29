import React, { useState, useEffect } from 'react';
import { Layout, Menu, Badge, Typography } from 'antd';
import { Link, useLocation } from 'react-router-dom';
import {
  DashboardOutlined,
  RobotOutlined,
  EnvironmentOutlined,
  ThunderboltOutlined,
  PlaySquareOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../store/fleetStore';
import { useSimulatorSignalR } from '../infrastructure/signalr/useSimulatorSignalR';

const { Sider, Content, Header } = Layout;
const { Text } = Typography;

const MENU_ITEMS = [
  { key: '/', icon: <DashboardOutlined />, label: 'Dashboard' },
  { key: '/fleet', icon: <RobotOutlined />, label: 'Fleet' },
  { key: '/map', icon: <EnvironmentOutlined />, label: 'Map Monitor' },
  { key: '/chaos', icon: <ThunderboltOutlined />, label: 'Chaos' },
  { key: '/scenario', icon: <PlaySquareOutlined />, label: 'Scenarios' },
  { key: '/settings', icon: <SettingOutlined />, label: 'Settings' },
];

interface Props { children: React.ReactNode; }

export const AppLayout: React.FC<Props> = ({ children }) => {
  const location = useLocation();
  const [collapsed, setCollapsed] = useState(false);
  const setFleet = useFleetStore((s) => s.setFleet);
  const addEvent = useFleetStore((s) => s.addEvent);
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const errorCount = agvs.filter((a) => a.status === 'ERROR').length;

  useSimulatorSignalR(['fleet'], {
    onFleetUpdated: (updatedAgvs) => setFleet(updatedAgvs),
    onFleetEvent: (event) => addEvent(event),
  });

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        collapsible
        collapsed={collapsed}
        onCollapse={setCollapsed}
        style={{ background: '#0d1117' }}
        width={200}
      >
        <div style={{ height: 48, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '0 12px' }}>
          <Text strong style={{ color: '#58a6ff', fontSize: collapsed ? 12 : 16, transition: 'all 0.2s' }}>
            {collapsed ? 'SIM' : 'AGV Simulator'}
          </Text>
        </div>
        <Menu
          theme="dark"
          selectedKeys={[location.pathname]}
          mode="inline"
          style={{ background: '#0d1117', borderRight: 0 }}
          items={MENU_ITEMS.map((item) => ({
            key: item.key,
            icon: item.key === '/fleet' && errorCount > 0
              ? <Badge count={errorCount} size="small">{item.icon}</Badge>
              : item.icon,
            label: <Link to={item.key}>{item.label}</Link>,
          }))}
        />
      </Sider>

      <Layout>
        <Header style={{ background: '#161b22', padding: '0 24px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', borderBottom: '1px solid #21262d' }}>
          <Text style={{ color: '#8b949e', fontSize: 13 }}>
            {agvs.length} AGV{agvs.length !== 1 ? 's' : ''} connected
            {errorCount > 0 && <Text style={{ color: '#f85149', marginLeft: 8 }}> · {errorCount} error{errorCount !== 1 ? 's' : ''}</Text>}
          </Text>
          <Text style={{ color: '#58a6ff', fontSize: 12 }}>ACS Simulator Web v1.0</Text>
        </Header>

        <Content style={{ padding: 24, background: '#0d1117', overflow: 'auto' }}>
          {children}
        </Content>
      </Layout>
    </Layout>
  );
};
