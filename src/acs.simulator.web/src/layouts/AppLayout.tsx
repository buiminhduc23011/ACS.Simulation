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
import { useLayoutStore } from '../store/layoutStore';
import { useSimulatorSignalR } from '../infrastructure/signalr/useSimulatorSignalR';
import { useTheme } from '../contexts/ThemeContext';
import { Switch, theme } from 'antd';
import { BulbOutlined, BulbFilled } from '@ant-design/icons';

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
  const headerLeft = useLayoutStore((s) => s.headerLeft);
  const { isDarkMode, toggleTheme } = useTheme();
  const { token } = theme.useToken();

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
        style={{ background: token.colorBgContainer }}
        width={200}
        theme={isDarkMode ? "dark" : "light"}
      >
        <div style={{ height: 48, display: 'flex', alignItems: 'center', justifyContent: 'center', padding: '0 12px' }}>
          <Text strong style={{ color: '#58a6ff', fontSize: collapsed ? 12 : 16, transition: 'all 0.2s' }}>
            {collapsed ? 'SIM' : 'AGV Simulator'}
          </Text>
        </div>
        <Menu
          theme={isDarkMode ? "dark" : "light"}
          selectedKeys={[location.pathname]}
          mode="inline"
          style={{ background: token.colorBgContainer, borderRight: 0 }}
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
        <Header style={{ background: token.colorBgElevated, padding: '0 24px', display: 'flex', alignItems: 'center', justifyContent: 'space-between', borderBottom: `1px solid ${token.colorBorder}` }}>
          <div style={{ display: 'flex', alignItems: 'center', flex: 1, minWidth: 0 }}>
            {headerLeft || (
              <Text style={{ color: token.colorTextSecondary, fontSize: 13 }}>
                {agvs.length} AGV{agvs.length !== 1 ? 's' : ''} connected
                {errorCount > 0 && <Text style={{ color: '#f85149', marginLeft: 8 }}> · {errorCount} error{errorCount !== 1 ? 's' : ''}</Text>}
              </Text>
            )}
          </div>
          <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
            <Switch
              checkedChildren={<BulbOutlined />}
              unCheckedChildren={<BulbFilled />}
              checked={!isDarkMode}
              onChange={toggleTheme}
            />
            <Text style={{ color: token.colorPrimary, fontSize: 12 }}>ACS Simulator Web v1.3.0</Text>
          </div>
        </Header>

        <Content style={{ padding: 24, background: isDarkMode ? '#0d1117' : '#f6f8fa', overflow: 'auto' }}>
          {children}
        </Content>
      </Layout>
    </Layout>
  );
};
