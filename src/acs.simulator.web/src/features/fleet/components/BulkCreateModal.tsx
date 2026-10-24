import React, { useState } from 'react';
import { Modal, Form, Input, InputNumber, Alert, Typography } from 'antd';
import type { IBulkCreateRequest, IBulkCreateResult } from '../../../types/agv';

const { Text } = Typography;

interface Props {
  open: boolean;
  onClose: () => void;
  onSubmit: (req: IBulkCreateRequest) => Promise<IBulkCreateResult>;
}

/** Tạo nhanh N AGV xếp lưới — gọi bulk-create của backend. */
export const BulkCreateModal: React.FC<Props> = ({ open, onClose, onSubmit }) => {
  const [form] = Form.useForm<IBulkCreateRequest>();
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<IBulkCreateResult | null>(null);

  const handleOk = async () => {
    const values = await form.validateFields();
    setLoading(true);
    try {
      const res = await onSubmit(values);
      setResult(res);
      if (res.failed === 0) {
        form.resetFields();
        onClose();
      }
    } finally {
      setLoading(false);
    }
  };

  const handleClose = () => {
    setResult(null);
    onClose();
  };

  return (
    <Modal
      title="Bulk Create AGVs"
      open={open}
      onCancel={handleClose}
      okText="Create"
      confirmLoading={loading}
      onOk={handleOk}
      width={520}
      destroyOnClose={false}
    >
      {result && result.failed > 0 && (
        <Alert
          type="warning"
          showIcon
          style={{ marginBottom: 16 }}
          message={`Created ${result.created}, failed ${result.failed}`}
          description={<Text style={{ fontSize: 12 }}>{result.errors.join(', ')}</Text>}
        />
      )}
      <Form
        form={form}
        layout="vertical"
        initialValues={{
          count: 10, serialPrefix: 'AGV-', startIndex: 1, serialNumberPadWidth: 3,
          manufacturer: 'stivn', mapId: '', originX: 0, originY: 0, theta: 0,
          spacing: 3, columns: 0,
        }}
      >
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', columnGap: 12 }}>
          <Form.Item name="count" label="Count" rules={[{ required: true }]}>
            <InputNumber min={1} max={100} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="manufacturer" label="Manufacturer" rules={[{ required: true }]}>
            <Input placeholder="stivn" />
          </Form.Item>
          <Form.Item name="serialPrefix" label="Serial Prefix" rules={[{ required: true }]}>
            <Input placeholder="AGV-" />
          </Form.Item>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', columnGap: 12 }}>
            <Form.Item name="startIndex" label="Start Index">
              <InputNumber min={0} style={{ width: '100%' }} />
            </Form.Item>
            <Form.Item name="serialNumberPadWidth" label="Pad Width">
              <InputNumber min={1} max={6} style={{ width: '100%' }} />
            </Form.Item>
          </div>
          <Form.Item name="mapId" label="Map ID">
            <Input placeholder="Map id (optional)" />
          </Form.Item>
          <Form.Item name="theta" label="Theta (rad)">
            <InputNumber step={0.1} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="originX" label="Origin X">
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="originY" label="Origin Y">
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="spacing" label="Spacing (m)">
            <InputNumber min={0.5} step={0.5} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="columns" label="Columns (0 = auto)" extra={<Text type="secondary" style={{ fontSize: 11 }}>Grid columns; 0 = √N</Text>}>
            <InputNumber min={0} style={{ width: '100%' }} />
          </Form.Item>
        </div>
      </Form>
    </Modal>
  );
};
