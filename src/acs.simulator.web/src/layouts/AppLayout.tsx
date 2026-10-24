import React, { useState } from 'react';
import { Layout, Menu, Badge, Typography, Flex, Button, Tooltip } from 'antd';
import { Link, useLocation } from 'react-router-dom';
import {
  DashboardOutlined,
  RobotOutlined,
  SettingOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  BulbOutlined,
  BulbFilled,
} from '@ant-design/icons';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../store/fleetStore';
import { useLayoutStore } from '../store/layoutStore';
import { useSimulatorSignalR } from '../infrastructure/signalr/useSimulatorSignalR';
import { useTheme } from '../contexts/ThemeContext';

const { Sider, Content, Header } = Layout;
const { Text } = Typography;

const MENU_ITEMS = [
  { key: '/', icon: <DashboardOutlined />, label: 'Dashboard' },
  { key: '/fleet', icon: <RobotOutlined />, label: 'Fleet' },
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
  const onlineCount = agvs.filter((a) => a.isConnected).length;
  const headerLeft = useLayoutStore((s) => s.headerLeft);
  const { isDarkMode, toggleTheme } = useTheme();

  useSimulatorSignalR(['fleet'], {
    onFleetUpdated: (updatedAgvs) => setFleet(updatedAgvs),
    onFleetEvent: (event) => addEvent(event),
  });

  const selectedKey = location.pathname.startsWith('/fleet') ? '/fleet' : location.pathname;

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        collapsible
        collapsed={collapsed}
        trigger={null}
        width={220}
        collapsedWidth={64}
        style={{
          background: 'var(--bg-container)',
          borderRight: '1px solid var(--border-color)',
          position: 'sticky',
          top: 0,
          height: '100vh',
          display: 'flex',
          flexDirection: 'column',
        }}
      >
        <Flex align="center" gap={10} style={{ height: 56, padding: collapsed ? '0 16px' : '0 20px', borderBottom: '1px solid var(--border-subtle)' }}>
          <Flex align="center" justify="center" style={{
            width: 32, height: 32, borderRadius: 8, flexShrink: 0,
            background: 'var(--primary-color)', color: '#fff', fontSize: 16,
          }}>
            <RobotOutlined />
          </Flex>
          {!collapsed && (
            <div style={{ lineHeight: 1.15, overflow: 'hidden' }}>
              <div style={{ fontWeight: 700, fontSize: 14, color: 'var(--text-main)', whiteSpace: 'nowrap' }}>ACS Simulator</div>
              <div style={{ fontSize: 10, color: 'var(--text-quaternary)', letterSpacing: '0.05em', whiteSpace: 'nowrap' }}>AGV FLEET TESTBED</div>
            </div>
          )}
        </Flex>

        <div style={{ flex: 1, overflowY: 'auto', paddingTop: 8 }}>
          <Menu
            selectedKeys={[selectedKey]}
            mode="inline"
            style={{ background: 'transparent', borderRight: 0, padding: '0 8px' }}
            items={MENU_ITEMS.map((item) => ({
              key: item.key,
              icon: item.key === '/fleet' && errorCount > 0
                ? <Badge count={errorCount} size="small" offset={[4, 0]}>{item.icon}</Badge>
                : item.icon,
              label: <Link to={item.key}>{item.label}</Link>,
            }))}
          />
        </div>

        <div style={{ padding: collapsed ? '12px 8px' : '12px 16px', borderTop: '1px solid var(--border-subtle)' }}>
          {!collapsed && (
            <Text style={{ fontSize: 10, color: 'var(--text-quaternary)', letterSpacing: '0.05em' }}>
              v1.4.2 · VDA5050
            </Text>
          )}
        </div>
      </Sider>

      <Layout style={{ background: 'var(--bg-layout)' }}>
        <Header style={{
          background: 'var(--bg-container)',
          padding: '0 20px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          borderBottom: '1px solid var(--border-color)',
          position: 'sticky',
          top: 0,
          zIndex: 100,
          height: 56,
          lineHeight: 'normal',
        }}>
          <Flex align="center" gap={12} style={{ flex: 1, minWidth: 0 }}>
            <Button
              type="text"
              icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
              onClick={() => setCollapsed(!collapsed)}
              style={{ color: 'var(--text-tertiary)' }}
            />
            {headerLeft || (
              <Flex align="center" gap={8}>
                <span className="status-badge info" style={{ textTransform: 'none' }}>
                  <span className="status-badge-dot" />
                  {onlineCount}/{agvs.length} online
                </span>
                {errorCount > 0 && (
                  <span className="status-badge error" style={{ textTransform: 'none' }}>
                    <span className="status-badge-dot" />
                    {errorCount} error{errorCount !== 1 ? 's' : ''}
                  </span>
                )}
              </Flex>
            )}
          </Flex>
          <Flex align="center" gap={8}>
            <Tooltip title={isDarkMode ? 'Switch to light mode' : 'Switch to dark mode'}>
              <Button
                type="text"
                icon={isDarkMode ? <BulbFilled /> : <BulbOutlined />}
                onClick={toggleTheme}
                style={{ color: 'var(--text-tertiary)' }}
              />
            </Tooltip>
          </Flex>
        </Header>

        <Content style={{ padding: '16px 20px 24px', overflow: 'auto', height: 'calc(100vh - 56px)' }}>
          {children}
        </Content>
      </Layout>
    </Layout>
  );
};
