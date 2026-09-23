import React, { Suspense, lazy, useState, useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ConfigProvider, Spin, theme } from 'antd';
import { ThemeContext } from './contexts/ThemeContext';
import { AppLayout } from './layouts/AppLayout';

const DashboardPage = lazy(() => import('./features/dashboard/pages/DashboardPage').then((m) => ({ default: m.DashboardPage })));
const FleetPage = lazy(() => import('./features/fleet/pages/FleetPage').then((m) => ({ default: m.FleetPage })));
const AgvControlPage = lazy(() => import('./features/fleet/pages/AgvControlPage').then((m) => ({ default: m.AgvControlPage })));
const MapMonitorPage = lazy(() => import('./features/map/pages/MapMonitorPage').then((m) => ({ default: m.MapMonitorPage })));
const ChaosPage = lazy(() => import('./features/chaos/pages/ChaosPage').then((m) => ({ default: m.ChaosPage })));
const ScenarioPage = lazy(() => import('./features/scenario/pages/ScenarioPage').then((m) => ({ default: m.ScenarioPage })));
const SettingsPage = lazy(() => import('./features/settings/pages/SettingsPage').then((m) => ({ default: m.SettingsPage })));

const loader = (
  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '60vh' }}>
    <Spin size="large" />
  </div>
);

export const App: React.FC = () => {
  const [isDarkMode, setIsDarkMode] = useState(() => {
    return localStorage.getItem('themeMode') !== 'light';
  });

  useEffect(() => {
    localStorage.setItem('themeMode', isDarkMode ? 'dark' : 'light');
  }, [isDarkMode]);

  const toggleTheme = () => setIsDarkMode(!isDarkMode);

  const currentToken = isDarkMode ? {
    colorPrimary: '#58a6ff',
    colorBgContainer: '#161b22',
    colorBgElevated: '#1c2128',
    colorBorder: '#21262d',
    colorText: '#c9d1d9',
    colorTextSecondary: '#8b949e',
    borderRadius: 6,
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
  } : {
    colorPrimary: '#0969da',
    colorBgContainer: '#ffffff',
    colorBgElevated: '#ffffff',
    colorBorder: '#d0d7de',
    colorText: '#24292f',
    colorTextSecondary: '#57606a',
    borderRadius: 6,
    fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
  };

  const currentAlgorithm = isDarkMode ? theme.darkAlgorithm : theme.defaultAlgorithm;

  return (
    <ThemeContext.Provider value={{ isDarkMode, toggleTheme }}>
      <ConfigProvider theme={{ algorithm: currentAlgorithm, token: currentToken }}>
    <BrowserRouter>
      <AppLayout>
        <Suspense fallback={loader}>
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/fleet" element={<FleetPage />} />
            <Route path="/fleet/:id/control" element={<AgvControlPage />} />
            <Route path="/map" element={<MapMonitorPage />} />
            <Route path="/chaos" element={<ChaosPage />} />
            <Route path="/scenario" element={<ScenarioPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Suspense>
      </AppLayout>
    </BrowserRouter>
  </ConfigProvider>
  </ThemeContext.Provider>
);
}
