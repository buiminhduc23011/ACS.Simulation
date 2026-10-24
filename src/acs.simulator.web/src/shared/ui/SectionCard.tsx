import React from 'react';
import { Card, CardProps, Typography } from 'antd';

export interface SectionCardProps extends CardProps {
  uiVariant?: 'default' | 'compact';
  noPadding?: boolean;
}

/** Card section chuẩn — title uppercase nhỏ, bo góc 8, shadow nhẹ (theo ACS). */
export const SectionCard: React.FC<SectionCardProps> = ({
  title,
  extra,
  children,
  className,
  uiVariant = 'default',
  noPadding = false,
  styles,
  ...props
}) => {
  const bodyPadding = noPadding ? 0 : uiVariant === 'compact' ? '12px 16px' : '16px 20px';

  return (
    <Card
      title={title ? (
        <Typography.Text
          style={{
            fontWeight: 700,
            fontSize: 12,
            color: 'var(--text-tertiary)',
            textTransform: 'uppercase',
            letterSpacing: '0.05em',
          }}
        >
          {title}
        </Typography.Text>
      ) : undefined}
      extra={extra}
      className={`section-card ${className || ''}`}
      styles={{
        ...styles,
        header: {
          padding: uiVariant === 'compact' ? '10px 16px' : '12px 20px',
          borderBottom: 'none',
          ...styles?.header,
        },
        body: { padding: bodyPadding, ...styles?.body },
      }}
      style={{ borderRadius: 8, boxShadow: 'var(--shadow-sm)', overflow: 'hidden', ...props.style }}
      {...props}
    >
      {children}
    </Card>
  );
};
