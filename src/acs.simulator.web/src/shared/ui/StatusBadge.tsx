import React from 'react';

export type StatusVariant = 'success' | 'info' | 'warning' | 'error' | 'neutral';

interface StatusBadgeProps {
  status?: string;
  text?: React.ReactNode;
  variant?: StatusVariant;
  className?: string;
  dot?: boolean;
}

const mapStatusToVariant = (s?: string): StatusVariant => {
  const v = String(s || '').toLowerCase();
  if (['active', 'online', 'success', 'ok', 'idle', 'automatic'].includes(v)) return 'success';
  if (['driving', 'running', 'executing', 'moving', 'info', 'charging'].includes(v)) return 'info';
  if (['warning', 'paused', 'manual', 'hold'].includes(v)) return 'warning';
  if (['error', 'fatal', 'failed', 'danger', 'critical'].includes(v)) return 'error';
  return 'neutral';
};

/** Pill badge trạng thái — variant màu qua CSS class status-badge (theo ACS). */
export const StatusBadge: React.FC<StatusBadgeProps> = ({ status, text, variant, className, dot = true }) => {
  const v = variant ?? mapStatusToVariant(status);
  return (
    <span className={`status-badge ${v} ${className || ''}`}>
      {dot && <span className="status-badge-dot" />}
      {text ?? status}
    </span>
  );
};
