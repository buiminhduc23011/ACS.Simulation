/**
 * Design tokens — đồng bộ với ACS web (slate palette + blue primary).
 * Dùng cho antd ConfigProvider; màu semantic map qua CSS vars trong global.css.
 */
export const UI_TOKENS = {
  colorPrimary: '#2563EB',
  colorPrimaryHover: '#1D4ED8',

  colorSuccess: '#16A34A',
  colorWarning: '#D97706',
  colorError: '#DC2626',
  colorInfo: '#2563EB',

  borderRadiusSm: 4,
  borderRadius: 6,
  borderRadiusMd: 8,
  borderRadiusLg: 10,

  fontFamilySans: 'Inter, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
  fontFamilyMono: '"JetBrains Mono", "Fira Code", Consolas, "Courier New", monospace',
} as const;

// Light (slate) — giống ACS
export const LIGHT_COLORS = {
  bgLayout: '#F8FAFC',
  bgContainer: '#FFFFFF',
  bgElevated: '#FFFFFF',
  textMain: '#0F172A',
  textSecondary: '#475569',
  textTertiary: '#64748B',
  textQuaternary: '#94A3B8',
  border: '#E2E8F0',
  borderSubtle: '#F1F5F9',
} as const;

// Dark (slate) — giống ACS dark mode
export const DARK_COLORS = {
  bgLayout: '#0F172A',
  bgContainer: '#1E293B',
  bgElevated: '#1E293B',
  textMain: '#F1F5F9',
  textSecondary: '#CBD5E1',
  textTertiary: '#94A3B8',
  textQuaternary: '#64748B',
  border: '#334155',
  borderSubtle: '#27364A',
} as const;
