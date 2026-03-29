import React, { useEffect, useState } from 'react';
import { Table, Button, Tag, Space, Popconfirm, Typography, Badge } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { PlusOutlined, EditOutlined, ControlOutlined } from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useFleetApi } from '../hooks/useFleetApi';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import type { ISimAgv } from '../../../types/agv';
import { CreateAgvModal } from '../components/CreateAgvModal';

const { Title } = Typography;

export const FleetPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs)));
  const { fetchFleet, createAgv, updateAgv, deleteAgv } = useFleetApi();
  const navigate = useNavigate();
  const [modalOpen, setModalOpen] = useState(false);
  const [editingAgv, setEditingAgv] = useState<ISimAgv | null>(null);

  useEffect(() => { fetchFleet(); }, [fetchFleet]);

  const handleOpenCreate = () => {
    setEditingAgv(null);
    setModalOpen(true);
  };

  const handleOpenEdit = (agv: ISimAgv) => {
    setEditingAgv(agv);
    setModalOpen(true);
  };

  const handleCloseModal = () => {
    setModalOpen(false);
    setEditingAgv(null);
  };

  const columns: ColumnsType<ISimAgv> = [
    {
      title: 'AGV ID', dataIndex: 'agvId', key: 'agvId',
      render: (_: string, rec) => (
        <Button type="link" onClick={() => navigate(`/fleet/${rec.id}/control`)} style={{ padding: 0 }}>{rec.id}</Button>
      ),
    },
    {
      title: 'Manufacturer', dataIndex: 'manufacturer', key: 'manufacturer',
    },
    {
      title: 'Serial Number', dataIndex: 'serialNumber', key: 'serialNumber',
    },
    {
      title: 'Status', dataIndex: 'status', key: 'status',
      render: (s: ISimAgv['status']) => <Tag color={AGV_STATUS_COLORS[s] ?? '#666'}>{s}</Tag>,
      filters: ['IDLE', 'DRIVING', 'ERROR', 'OFFLINE', 'CHARGING'].map((v) => ({ text: v, value: v })),
      onFilter: (v, r) => r.status === v,
    },
    { title: 'X', dataIndex: 'posX', key: 'posX', render: (v: number) => v?.toFixed(2) },
    { title: 'Y', dataIndex: 'posY', key: 'posY', render: (v: number) => v?.toFixed(2) },
    {
      title: 'Battery', dataIndex: 'batteryLevel', key: 'batteryLevel',
      render: (v: number) => {
        const color = v > 50 ? '#52c41a' : v > 20 ? '#faad14' : '#ff4d4f';
        return <span style={{ color }}>{v?.toFixed(0)}%</span>;
      },
      sorter: (a: ISimAgv, b: ISimAgv) => (a.batteryLevel ?? 0) - (b.batteryLevel ?? 0),
    },
    {
      title: 'Speed', key: 'speed',
      render: (_: unknown, rec) => `${Math.hypot(rec.speedX ?? 0, rec.speedY ?? 0).toFixed(2)} m/s`,
    },
    {
      title: 'Errors', dataIndex: 'errorCount', key: 'errorCount',
      render: (v: number) => v > 0 ? <Badge count={v} /> : <span>—</span>,
    },
    {
      title: 'Actions', key: 'actions',
      render: (_, rec) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => handleOpenEdit(rec)}>Edit</Button>
          <Button size="small" icon={<ControlOutlined />} onClick={() => navigate(`/fleet/${rec.id}/control`)}>Control</Button>
          <Popconfirm title={`Delete ${rec.id}?`} onConfirm={() => deleteAgv(rec.id)} okText="Delete" okType="danger">
            <Button size="small" danger>Delete</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <Title level={4} style={{ margin: 0 }}>Fleet Management</Title>
        <Button type="primary" icon={<PlusOutlined />} onClick={handleOpenCreate}>
          Add AGV
        </Button>
      </div>

      <Table
        dataSource={agvs}
        columns={columns}
        rowKey="id"
        size="small"
        pagination={{ pageSize: 20 }}
        rowClassName={(r) => r.status === 'ERROR' ? 'row-error' : r.status === 'OFFLINE' ? 'row-offline' : ''}
      />

      <CreateAgvModal
        open={modalOpen}
        onClose={handleCloseModal}
        onCreate={createAgv}
        onUpdate={updateAgv}
        editingAgv={editingAgv}
      />
    </div>
  );
};
