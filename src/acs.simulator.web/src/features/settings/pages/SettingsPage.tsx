import React, { useEffect, useState } from 'react';
import { Card, Form, Input, InputNumber, Button, Row, Col, Typography, message, Divider } from 'antd';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { useConfigStore } from '../../../store/configStore';

const { Title } = Typography;

export const SettingsPage: React.FC = () => {
  const { mqtt, acsApi, setConfig } = useConfigStore();
  const [mqttForm] = Form.useForm();
  const [acsForm] = Form.useForm();
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    apiClient.get(ENDPOINTS.config.get)
      .then((r) => {
        const data = r.data as { mqtt: typeof mqtt; acsApi: typeof acsApi };
        setConfig(data);
        mqttForm.setFieldsValue(data.mqtt);
        acsForm.setFieldsValue(data.acsApi);
      })
      .catch(console.error);
  }, []);

  const saveMqtt = async () => {
    const values = await mqttForm.validateFields();
    setLoading(true);
    try {
      await apiClient.put(ENDPOINTS.config.updateMqtt, values);
      message.success('MQTT config saved');
    } finally { setLoading(false); }
  };

  const saveAcsApi = async () => {
    const values = await acsForm.validateFields();
    setLoading(true);
    try {
      await apiClient.put(ENDPOINTS.config.updateAcsApi, values);
      message.success('ACS API config saved');
    } finally { setLoading(false); }
  };

  return (
    <div>
      <Title level={4} style={{ marginBottom: 16 }}>Settings</Title>
      <Row gutter={[24, 24]}>
        <Col xs={24} md={12}>
          <Card title="MQTT Broker">
            <Form form={mqttForm} layout="vertical" initialValues={mqtt}>
              <Form.Item name="host" label="Host" rules={[{ required: true }]}>
                <Input placeholder="127.0.0.1" />
              </Form.Item>
              <Form.Item name="port" label="Port" rules={[{ required: true }]}>
                <InputNumber style={{ width: '100%' }} min={1} max={65535} />
              </Form.Item>
              <Button type="primary" onClick={saveMqtt} loading={loading}>Save MQTT</Button>
            </Form>
          </Card>
        </Col>

        <Col xs={24} md={12}>
          <Card title="ACS API">
            <Form form={acsForm} layout="vertical" initialValues={acsApi}>
              <Form.Item name="baseUrl" label="Base URL" rules={[{ required: true }]}>
                <Input placeholder="http://localhost:9050" />
              </Form.Item>
              <Button type="primary" onClick={saveAcsApi} loading={loading}>Save ACS API</Button>
            </Form>

            <Divider />
            <Typography.Text type="secondary">
              The ACS API is used to fetch available maps from the main system.
              Make sure the ACS backend is running on the configured URL.
            </Typography.Text>
          </Card>
        </Col>
      </Row>
    </div>
  );
};
