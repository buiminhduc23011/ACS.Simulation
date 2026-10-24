import React from 'react';
import { Col, Flex, Row, Space, Statistic, Tooltip, Typography } from 'antd';

const { Title, Text } = Typography;

export interface IPageStat {
  title: string;
  value: string | number;
  color?: string;
  icon?: React.ReactNode;
  tooltip?: React.ReactNode;
}

interface PageHeaderProps {
  title: React.ReactNode;
  subtitle?: React.ReactNode;
  icon?: React.ReactNode;
  backNode?: React.ReactNode;
  stats?: IPageStat[];
  actions?: React.ReactNode;
  style?: React.CSSProperties;
}

/** Header chuẩn của trang quản lý — icon + title/subtitle trái, stats + actions phải (theo ACS). */
export const PageHeader: React.FC<PageHeaderProps> = ({
  title,
  subtitle,
  icon,
  backNode,
  stats = [],
  actions,
  style,
}) => (
  <div
    className="page-header"
    style={{
      padding: '8px 0 16px',
      flexShrink: 0,
      ...style,
    }}
  >
    <Row justify="space-between" align="middle" gutter={[12, 12]} wrap={false}>
      <Col>
        <Space size={12} align="center">
          {backNode}
          {icon && (
            <Flex
              align="center"
              justify="center"
              style={{
                width: 36,
                height: 36,
                borderRadius: 8,
                background: 'var(--primary-bg)',
                color: 'var(--primary-color)',
                fontSize: 18,
                border: '1px solid var(--primary-border)',
                flexShrink: 0,
              }}
            >
              {icon}
            </Flex>
          )}
          <Space direction="vertical" size={0}>
            <Title
              level={1}
              style={{
                margin: 0,
                fontWeight: 700,
                fontSize: 18,
                color: 'var(--text-main)',
                letterSpacing: '-0.01em',
                whiteSpace: 'nowrap',
                lineHeight: 1.2,
              }}
            >
              {title}
            </Title>
            {subtitle && (
              <Text style={{ fontSize: 12, color: 'var(--text-secondary)', whiteSpace: 'nowrap', lineHeight: 1.2 }}>
                {subtitle}
              </Text>
            )}
          </Space>
        </Space>
      </Col>

      <Col>
        <Flex align="center" gap={16}>
          {stats.length > 0 && (
            <Flex gap={12} align="center">
              {stats.map((stat, index) => (
                <React.Fragment key={index}>
                  <Statistic
                    title={stat.tooltip ? (
                      <Tooltip title={stat.tooltip}>
                        <Text className="stat-title">{stat.title}</Text>
                      </Tooltip>
                    ) : (
                      <Text className="stat-title">{stat.title}</Text>
                    )}
                    value={stat.value}
                    prefix={stat.icon && <span style={{ marginRight: 6, fontSize: 14, opacity: 0.8 }}>{stat.icon}</span>}
                    valueStyle={{ color: stat.color || 'var(--text-main)', fontSize: 18, fontWeight: 700, lineHeight: 1.1 }}
                  />
                  {index < stats.length - 1 && (
                    <div style={{ width: 1, height: 24, background: 'var(--border-color)', opacity: 0.5 }} />
                  )}
                </React.Fragment>
              ))}
            </Flex>
          )}
          {actions && <div>{actions}</div>}
        </Flex>
      </Col>
    </Row>
  </div>
);
