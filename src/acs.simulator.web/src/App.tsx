import React, { Suspense, lazy, useState, useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { ConfigProvider, Spin, theme } from 'antd';
import { ThemeContext } from './contexts/ThemeContext';
import { AppLayout } from './layouts/AppLayout';
import { DARK_COLORS, LIGHT_COLORS, UI_TOKENS } from './shared/ui/tokens';

const DashboardPage = lazy(() => import('./features/dashboard/pages/DashboardPage').then((m) => ({ default: m.DashboardPage })));
const FleetPage = lazy(() => import('./features/fleet/pages/FleetPage').then((m) => ({ default: m.FleetPage })));
const AgvControlPage = lazy(() => import('./features/fleet/pages/AgvControlPage').then((m) => ({ default: m.AgvControlPage })));
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
    document.documentElement.dataset.theme = isDarkMode ? 'dark' : 'light';
  }, [isDarkMode]);

  const toggleTheme = () => setIsDarkMode(!isDarkMode);
  const colors = isDarkMode ? DARK_COLORS : LIGHT_COLORS;

  return (
    <ThemeContext.Provider value={{ isDarkMode, toggleTheme }}>
      <ConfigProvider
        theme={{
          algorithm: isDarkMode ? theme.darkAlgorithm : theme.defaultAlgorithm,
          token: {
            colorPrimary: UI_TOKENS.colorPrimary,
            colorSuccess: UI_TOKENS.colorSuccess,
            colorWarning: UI_TOKENS.colorWarning,
            colorError: UI_TOKENS.colorError,
            colorInfo: UI_TOKENS.colorInfo,
            colorBgLayout: colors.bgLayout,
            colorBgContainer: colors.bgContainer,
            colorBgElevated: colors.bgElevated,
            colorBorder: colors.border,
            colorBorderSecondary: colors.borderSubtle,
            colorText: colors.textMain,
            colorTextSecondary: colors.textSecondary,
            colorTextTertiary: colors.textTertiary,
            colorTextQuaternary: colors.textQuaternary,
            borderRadius: UI_TOKENS.borderRadius,
            borderRadiusLG: UI_TOKENS.borderRadiusMd,
            borderRadiusSM: UI_TOKENS.borderRadiusSm,
            fontFamily: UI_TOKENS.fontFamilySans,
            fontFamilyCode: UI_TOKENS.fontFamilyMono,
          },
          components: {
            Layout: {
              headerBg: colors.bgContainer,
              siderBg: colors.bgContainer,
              bodyBg: colors.bgLayout,
              headerHeight: 56,
              headerPadding: '0 20px',
            },
            Card: { borderRadiusLG: UI_TOKENS.borderRadiusMd },
            Button: { borderRadius: UI_TOKENS.borderRadius, fontWeight: 500 },
            Menu: { itemBg: 'transparent' },
            Table: {
              headerBg: colors.bgLayout,
              headerColor: colors.textTertiary,
              headerSplitColor: 'transparent',
            },
            Modal: { borderRadiusLG: UI_TOKENS.borderRadiusLg },
            Tag: { borderRadiusSM: UI_TOKENS.borderRadiusSm },
            Statistic: { contentFontSize: 24 },
          },
        }}
      >
        <BrowserRouter>
          <AppLayout>
            <Suspense fallback={loader}>
              <Routes>
                <Route path="/" element={<DashboardPage />} />
                <Route path="/fleet" element={<FleetPage />} />
                <Route path="/fleet/:id/control" element={<AgvControlPage />} />
                <Route path="/settings" element={<SettingsPage />} />
                <Route path="*" element={<Navigate to="/" replace />} />
              </Routes>
            </Suspense>
          </AppLayout>
        </BrowserRouter>
      </ConfigProvider>
    </ThemeContext.Provider>
  );
};
