import React, { useEffect, useState } from 'react';
import { Row, Col, Card, Select, Slider, Button, Form, Table, Tag, Space, Typography, message, Divider } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import type { IErrorTemplate, IChaosSettings } from '../../../types/chaos';

const { Title, Text } = Typography;

interface ActiveError {
  agvId: string;
  errorType: string;
  errorDescription: string;
  errorLevel: string;
}

export const ChaosPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const [templates, setTemplates] = useState<IErrorTemplate[]>([]);
  const [selectedAgv, setSelectedAgv] = useState<string | null>(null);
  const [chaosSettings, setChaosSettings] = useState<IChaosSettings>({ latencyMinMs: 0, latencyMaxMs: 0, packetLossPercent: 0 });
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    apiClient.get<IErrorTemplate[]>(ENDPOINTS.errorTemplates)
      .then((r) => setTemplates(r.data))
      .catch(console.error);
  }, []);

  const injectTemplate = async (templateId: string) => {
    if (!selectedAgv) { message.warning('Select an AGV first'); return; }
    setLoading(true);
    try {
      await apiClient.post(ENDPOINTS.control.injectTemplate(selectedAgv), { templateId });
      message.success('Error injected');
    } finally { setLoading(false); }
  };

  const applyChaos = async () => {
    if (!selectedAgv) { message.warning('Select an AGV first'); return; }
    setLoading(true);
    try {
      await apiClient.post(ENDPOINTS.control.chaos(selectedAgv), chaosSettings);
      message.success('Chaos settings applied');
    } finally { setLoading(false); }
  };

  const clearAllErrors = async () => {
    if (!selectedAgv) { message.warning('Select an AGV first'); return; }
    setLoading(true);
    try {
      await apiClient.delete(ENDPOINTS.control.clearErrors(selectedAgv));
      message.success('Errors cleared');
    } finally { setLoading(false); }
  };

  const disconnect = async () => {
    if (!selectedAgv) { message.warning('Select an AGV first'); return; }
    await apiClient.post(ENDPOINTS.control.disconnect(selectedAgv));
    message.warning(`AGV ${selectedAgv} disconnected`);
  };

  const activeErrors: ActiveError[] = agvs.flatMap((a) =>
    (a.errors ?? []).map((e) => ({ agvId: a.id, errorType: e.errorType, errorDescription: e.description ?? '', errorLevel: e.errorLevel }))
  );

  const errorColumns: ColumnsType<ActiveError> = [
    { title: 'AGV', dataIndex: 'agvId', key: 'agvId', render: (v) => <Tag>{v}</Tag> },
    { title: 'Type', dataIndex: 'errorType', key: 'errorType' },
    { title: 'Description', dataIndex: 'errorDescription', key: 'errorDescription' },
    {
      title: 'Level', dataIndex: 'errorLevel', key: 'errorLevel',
      render: (v) => <Tag color={v === 'FATAL' ? 'red' : 'orange'}>{v}</Tag>,
    },
  ];

  return (
    <div>
      <Title level={4} style={{ marginBottom: 16 }}>Chaos Engineering</Title>

      <Row gutter={[16, 16]}>
        {/* Left: Controls */}
        <Col xs={24} lg={14}>
          <Card title="Target AGV">
            <Select
              style={{ width: '100%', marginBottom: 16 }}
              placeholder="Select AGV to target"
              value={selectedAgv}
              onChange={setSelectedAgv}
              options={agvs.map((a) => ({ label: `${a.id} [${a.status}]`, value: a.id }))}
            />

            <Divider orientation="left">Error Templates</Divider>
            <Row gutter={[8, 8]}>
              {templates.map((t) => (
                <Col key={t.id} xs={12} sm={8}>
                  <Card
                    size="small"
                    hoverable
                    onClick={() => injectTemplate(t.id)}
                    style={{ cursor: 'pointer', borderColor: t.errorLevel === 'FATAL' ? '#ff4d4f' : '#faad14' }}
                  >
                    <Text strong style={{ fontSize: 12 }}>{t.name}</Text>
                    <br />
                    <Tag color={t.errorLevel === 'FATAL' ? 'red' : 'orange'} style={{ fontSize: 10 }}>{t.errorLevel}</Tag>
                    <br />
                    <Text type="secondary" style={{ fontSize: 11 }}>{t.errorDescription}</Text>
                  </Card>
                </Col>
              ))}
              {templates.length === 0 && <Col span={24}><Text type="secondary">No templates available</Text></Col>}
            </Row>

            <Divider orientation="left">Network Chaos</Divider>
            <Form layout="vertical">
              <Row gutter={16}>
                <Col span={12}>
                  <Form.Item label={`Latency Min: ${chaosSettings.latencyMinMs}ms`}>
                    <Slider min={0} max={5000} value={chaosSettings.latencyMinMs}
                      onChange={(v) => setChaosSettings((s) => ({ ...s, latencyMinMs: v }))} />
                  </Form.Item>
                </Col>
                <Col span={12}>
                  <Form.Item label={`Latency Max: ${chaosSettings.latencyMaxMs}ms`}>
                    <Slider min={0} max={5000} value={chaosSettings.latencyMaxMs}
                      onChange={(v) => setChaosSettings((s) => ({ ...s, latencyMaxMs: v }))} />
                  </Form.Item>
                </Col>
                <Col span={12}>
                  <Form.Item label={`Packet Loss: ${chaosSettings.packetLossPercent}%`}>
                    <Slider min={0} max={100} value={chaosSettings.packetLossPercent}
                      onChange={(v) => setChaosSettings((s) => ({ ...s, packetLossPercent: v }))} />
                  </Form.Item>
                </Col>
              </Row>
              <Space>
                <Button type="primary" onClick={applyChaos} loading={loading}>Apply Chaos</Button>
                <Button onClick={() => {
                  setChaosSettings({ latencyMinMs: 0, latencyMaxMs: 0, packetLossPercent: 0 });
                  if (selectedAgv) apiClient.post(ENDPOINTS.control.chaos(selectedAgv), { latencyMinMs: 0, latencyMaxMs: 0, packetLossPercent: 0 });
                }}>Reset</Button>
                <Button danger onClick={clearAllErrors} loading={loading}>Clear All Errors</Button>
                <Button danger onClick={disconnect}>Disconnect</Button>
              </Space>
            </Form>
          </Card>
        </Col>

        {/* Right: Active Errors */}
        <Col xs={24} lg={10}>
          <Card title={`Active Errors (${activeErrors.length})`}>
            <Table
              dataSource={activeErrors}
              columns={errorColumns}
              rowKey={(r) => `${r.agvId}-${r.errorType}`}
              size="small"
              pagination={{ pageSize: 10 }}
              locale={{ emptyText: 'No active errors' }}
            />
          </Card>
        </Col>
      </Row>
    </div>
  );
};
