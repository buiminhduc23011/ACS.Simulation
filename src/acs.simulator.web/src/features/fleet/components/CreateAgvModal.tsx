import React, { useEffect } from 'react';
import { Modal, Form, Input, InputNumber, Select, Button, AutoComplete, Space, Typography } from 'antd';
import { MinusCircleOutlined, PlusOutlined } from '@ant-design/icons';
import type { ICreateAgvRequest, ISimAgv } from '../../../types/agv';
import type { IMapSummary } from '../../../types/map';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';

const { Text } = Typography;

interface Props {
  open: boolean;
  onClose: () => void;
  onCreate: (req: ICreateAgvRequest) => Promise<void>;
  onUpdate?: (id: string, req: ICreateAgvRequest) => Promise<void>;
  editingAgv?: ISimAgv | null;
}

export const CreateAgvModal: React.FC<Props> = ({ open, onClose, onCreate, onUpdate, editingAgv }) => {
  const [form] = Form.useForm<ICreateAgvRequest>();
  const [loading, setLoading] = React.useState(false);
  const [mapOptions, setMapOptions] = React.useState<{ value: string; label: string }[]>([]);
  const isEdit = !!editingAgv;

  useEffect(() => {
    apiClient.get<IMapSummary[]>(ENDPOINTS.maps.list)
      .then(res => setMapOptions(res.data.map(m => ({ value: m.mapId, label: `${m.mapId} — ${m.mapName}` }))))
      .catch(() => { /* ACS not connected */ });
  }, []);

  useEffect(() => {
    if (open && editingAgv) {
      form.setFieldsValue({
        serialNumber: editingAgv.serialNumber,
        manufacturer: editingAgv.manufacturer,
        posX: editingAgv.posX,
        posY: editingAgv.posY,
        posTheta: editingAgv.posTheta,
        mapId: editingAgv.mapId,
        ipAddress: editingAgv.ipAddress,
        macAddress: editingAgv.macAddress,
        mapMappings: editingAgv.mapMappings ?? [],
      });
    } else if (open) {
      form.resetFields();
    }
  }, [open, editingAgv, form]);

  const handleOk = async () => {
    const values = await form.validateFields();
    setLoading(true);
    try {
      if (isEdit && onUpdate) {
        await onUpdate(editingAgv.id, values);
      } else {
        await onCreate(values);
      }
      form.resetFields();
      onClose();
    } finally {
      setLoading(false);
    }
  };

  return (
    <Modal
      title={isEdit ? `Edit AGV: ${editingAgv.serialNumber}` : 'Create AGV'}
      open={open}
      onCancel={onClose}
      footer={[
        <Button key="cancel" onClick={onClose}>Cancel</Button>,
        <Button key="ok" type="primary" loading={loading} onClick={handleOk}>
          {isEdit ? 'Update' : 'Create'}
        </Button>,
      ]}
    >
      <Form
        form={form}
        layout="vertical"
        initialValues={{ posX: 0, posY: 0, posTheta: 0, mapId: '', batteryLevel: 100, mapMappings: [] }}
      >
        <Form.Item name="serialNumber" label="Serial Number (AGV ID)" rules={[{ required: true }]}>
          <Input placeholder="e.g. SN001" />
        </Form.Item>
        <Form.Item name="manufacturer" label="Manufacturer" rules={[{ required: true }]}>
          <Input placeholder="e.g. TestManufacturer" />
        </Form.Item>
        <Form.Item label="Position" style={{ marginBottom: 0 }}>
          <Form.Item name="posX" label="X" style={{ display: 'inline-block', width: '32%', marginRight: '2%' }}>
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="posY" label="Y" style={{ display: 'inline-block', width: '32%', marginRight: '2%' }}>
            <InputNumber style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="posTheta" label="Theta (rad)" style={{ display: 'inline-block', width: '32%' }}>
            <InputNumber style={{ width: '100%' }} step={0.1} min={-Math.PI} max={Math.PI} />
          </Form.Item>
        </Form.Item>
        <Form.Item name="mapId" label="Map ID" rules={[{ required: true, message: 'Map ID is required' }]}>
          <AutoComplete
            options={mapOptions}
            placeholder={mapOptions.length ? 'Select or type map ID' : 'Type map ID (e.g. floor1)'}
            filterOption={(input, opt) => (opt?.value ?? '').toLowerCase().includes(input.toLowerCase())}
            allowClear
          />
        </Form.Item>
        <Form.Item label="Map Mapping" style={{ marginBottom: 12 }}>
          <Text type="secondary">
            Optional. Translate ACS map IDs to the simulator map ID this AGV should actually use.
          </Text>
          <Form.List name="mapMappings">
            {(fields, { add, remove }) => (
              <>
                <div style={{ marginTop: 12, display: 'grid', gap: 12 }}>
                  {fields.map((field) => (
                    <Space key={field.key} align="start" style={{ display: 'flex' }}>
                      <Form.Item
                        {...field}
                        name={[field.name, 'sourceMapId']}
                        label="ACS Map ID"
                        rules={[{ required: true, message: 'Source map is required' }]}
                        style={{ minWidth: 220, marginBottom: 0 }}
                      >
                        <AutoComplete
                          options={mapOptions}
                          placeholder="e.g. 1 or ACS_MAP_01"
                          filterOption={(input, opt) => (opt?.value ?? '').toLowerCase().includes(input.toLowerCase())}
                          allowClear
                        />
                      </Form.Item>
                      <Form.Item
                        {...field}
                        name={[field.name, 'targetMapId']}
                        label="Simulator Map ID"
                        rules={[{ required: true, message: 'Target map is required' }]}
                        style={{ minWidth: 220, marginBottom: 0 }}
                      >
                        <AutoComplete
                          options={mapOptions}
                          placeholder="e.g. FLOOR_1_SIM"
                          filterOption={(input, opt) => (opt?.value ?? '').toLowerCase().includes(input.toLowerCase())}
                          allowClear
                        />
                      </Form.Item>
                      <Button
                        danger
                        type="text"
                        icon={<MinusCircleOutlined />}
                        onClick={() => remove(field.name)}
                        style={{ marginTop: 30 }}
                      />
                    </Space>
                  ))}
                </div>
                <Button
                  type="dashed"
                  icon={<PlusOutlined />}
                  onClick={() => add({ sourceMapId: '', targetMapId: '' })}
                  style={{ marginTop: 12, width: '100%' }}
                >
                  Add Map Mapping
                </Button>
              </>
            )}
          </Form.List>
        </Form.Item>
        {!isEdit && (
          <Form.Item name="batteryLevel" label="Initial Battery (%)">
            <InputNumber min={0} max={100} style={{ width: '100%' }} />
          </Form.Item>
        )}
        {!isEdit && (
          <>
            <Form.Item name="vehicleShapeId" label="Vehicle Shape" initialValue={1}>
              <Select options={[
                { label: 'Carrier (Default)', value: 1 },
                { label: 'Forklift', value: 2 },
                { label: 'Conveyor', value: 3 },
                { label: 'Tugger', value: 4 },
              ]} />
            </Form.Item>
            <Form.Item name="navigationTechnologyId" label="Navigation Technology" initialValue={1}>
              <Select options={[
                { label: 'Natural Navigation (Default)', value: 1 },
                { label: 'Laser Navigation', value: 2 },
                { label: 'Magnetic Tape', value: 3 },
                { label: 'QR Code', value: 4 },
              ]} />
            </Form.Item>
          </>
        )}
        <Form.Item name="ipAddress" label="IP Address">
          <Input placeholder="e.g. 192.168.1.100 (optional)" />
        </Form.Item>
        <Form.Item name="macAddress" label="MAC Address">
          <Input placeholder="e.g. 00:11:22:33:44:55 (optional)" />
        </Form.Item>
      </Form>
    </Modal>
  );
};
