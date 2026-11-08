import React, { useState } from 'react';
import { Modal, Form, InputNumber, Switch, Typography, Divider, Space, Button } from 'antd';

const { Text } = Typography;

export interface ApplyAllValues {
  applyMotion: boolean;
  maxSpeed: number;
  acceleration: number;
  deceleration: number;
  applyActions: boolean;
  liftDurationMs: number;
  lowerDurationMs: number;
  genericActionDurationMs: number;
  loadStateDelayMs: number;
  applyBattery: boolean;
  fullChargeHours: number;
  applyBatteryDrain: boolean;
  continuousOperatingHours: number;
}

interface Props {
  open: boolean;
  targetCount: number;
  hasSelection: boolean;
  onClose: () => void;
  onSubmit: (values: ApplyAllValues, scope: 'selected' | 'all') => Promise<void>;
}

/** Apply nhanh 1 bộ thông số cho nhiều AGV: chọn scope selected/all + tick từng nhóm. */
export const ApplyAllConfigModal: React.FC<Props> = ({ open, targetCount, hasSelection, onClose, onSubmit }) => {
  const [form] = Form.useForm<ApplyAllValues>();
  const [loading, setLoading] = useState(false);
  const [applyMotion, setApplyMotion] = useState(true);
  const [applyActions, setApplyActions] = useState(true);
  const [applyBattery, setApplyBattery] = useState(true);
  const [applyBatteryDrain, setApplyBatteryDrain] = useState(true);

  const handleOk = async (scope: 'selected' | 'all') => {
    const values = await form.validateFields();
    setLoading(true);
    try {
      await onSubmit({ ...values, applyMotion, applyActions, applyBattery, applyBatteryDrain }, scope);
      onClose();
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title={`Apply config ${hasSelection ? 'cho selection / toàn fleet' : 'cho toàn fleet'}`}
      open={open}
      onCancel={onClose}
      width={560}
      footer={null}
      destroyOnClose={false}
    >
      <Text type="secondary" style={{ fontSize: 12 }}>
        Tick nhóm nào thì áp nhóm đó. Tuyến tính pin: tốc độ nạp = 100 / (giờ * 60) %/phút; tốc độ xả khi chạy = 100 / (giờ chạy * 60) %/phút.
        {hasSelection ? ` Đang chọn ${targetCount} AGV.` : ' Chưa chọn AGV nào — sẽ áp toàn fleet.'}
      </Text>
      <Form
        form={form}
        layout="vertical"
        style={{ marginTop: 12 }}
        initialValues={{
          maxSpeed: 1.5, acceleration: 0.8, deceleration: 0.8,
          liftDurationMs: 2000, lowerDurationMs: 2000, genericActionDurationMs: 3500, loadStateDelayMs: 250,
          fullChargeHours: 5.6,
          continuousOperatingHours: 8,
        }}
      >
        <Space align="center" style={{ width: '100%', justifyContent: 'space-between' }}>
          <Text strong>Tốc độ / gia tốc</Text>
          <Switch size="small" checked={applyMotion} onChange={setApplyMotion} />
        </Space>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', columnGap: 12, opacity: applyMotion ? 1 : 0.4, pointerEvents: applyMotion ? 'auto' : 'none' }}>
          <Form.Item name="maxSpeed" label="Tốc max (m/s)" rules={[{ required: applyMotion }]}>
            <InputNumber min={0.05} max={10} step={0.1} precision={2} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="acceleration" label="Gia tốc tăng (m/s²)" rules={[{ required: applyMotion }]}>
            <InputNumber min={0.05} max={10} step={0.1} precision={2} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="deceleration" label="Gia tốc giảm (m/s²)" rules={[{ required: applyMotion }]}>
            <InputNumber min={0.05} max={10} step={0.1} precision={2} style={{ width: '100%' }} />
          </Form.Item>
        </div>

        <Divider style={{ margin: '8px 0' }} />
        <Space align="center" style={{ width: '100%', justifyContent: 'space-between' }}>
          <Text strong>Thời gian trao / nhận hàng (PICK/DROP)</Text>
          <Switch size="small" checked={applyActions} onChange={setApplyActions} />
        </Space>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', columnGap: 12, opacity: applyActions ? 1 : 0.4, pointerEvents: applyActions ? 'auto' : 'none' }}>
          <Form.Item name="liftDurationMs" label="PICK / LIFT (ms)" rules={[{ required: applyActions }]}>
            <InputNumber min={0} max={600000} step={100} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="lowerDurationMs" label="DROP / LOWER (ms)" rules={[{ required: applyActions }]}>
            <InputNumber min={0} max={600000} step={100} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="genericActionDurationMs" label="Action khác (ms)" rules={[{ required: applyActions }]}>
            <InputNumber min={0} max={600000} step={100} style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="loadStateDelayMs" label="Delay cập nhật load (ms)" rules={[{ required: applyActions }]}>
            <InputNumber min={0} max={60000} step={50} style={{ width: '100%' }} />
          </Form.Item>
        </div>

        <Divider style={{ margin: '8px 0' }} />
        <Space align="center" style={{ width: '100%', justifyContent: 'space-between' }}>
          <Text strong>Thời gian sạc đầy 0→100%</Text>
          <Switch size="small" checked={applyBattery} onChange={setApplyBattery} />
        </Space>
        <div style={{ opacity: applyBattery ? 1 : 0.4, pointerEvents: applyBattery ? 'auto' : 'none' }}>
          <Form.Item
            name="fullChargeHours"
            label="Sạc đầy (giờ, float vd 5.6)"
            rules={[{ required: applyBattery }]}
            extra={<Text type="secondary" style={{ fontSize: 11 }}>vd 5.6h → nạp tuyến tính ≈ 0.298%/phút</Text>}
          >
            <InputNumber min={0.01} max={1000} step={0.1} precision={2} style={{ width: '100%' }} addonAfter="giờ" />
          </Form.Item>
        </div>

        <Divider style={{ margin: '8px 0' }} />
        <Space align="center" style={{ width: '100%', justifyContent: 'space-between' }}>
          <Text strong>Thời gian chạy liên tục 100→0%</Text>
          <Switch size="small" checked={applyBatteryDrain} onChange={setApplyBatteryDrain} />
        </Space>
        <div style={{ opacity: applyBatteryDrain ? 1 : 0.4, pointerEvents: applyBatteryDrain ? 'auto' : 'none' }}>
          <Form.Item
            name="continuousOperatingHours"
            label="Chạy liên tục (giờ, float vd 8)"
            rules={[{ required: applyBatteryDrain }]}
            extra={<Text type="secondary" style={{ fontSize: 11 }}>vd 8h → xả khi chạy ≈ 0.208%/phút</Text>}
          >
            <InputNumber min={0.01} max={1000} step={0.5} precision={2} style={{ width: '100%' }} addonAfter="giờ" />
          </Form.Item>
        </div>

        <Space style={{ width: '100%', justifyContent: 'flex-end', marginTop: 8 }}>
          <Button onClick={onClose}>Hủy</Button>
          {hasSelection && (
            <Button onClick={() => void handleOk('selected')} loading={loading}>
              {`Áp cho ${targetCount} đã chọn`}
            </Button>
          )}
          <Button
            type="primary"
            onClick={() => void handleOk('all')}
            loading={loading}
          >
            Áp toàn fleet
          </Button>
        </Space>
      </Form>
    </Modal>
  );
};
