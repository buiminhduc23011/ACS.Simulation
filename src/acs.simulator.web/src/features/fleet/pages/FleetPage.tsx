import React, { useEffect, useState } from 'react';
import { Table, Button, Tag, Space, Popconfirm, Typography, Badge, Tooltip } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { PlusOutlined, EditOutlined, ControlOutlined, PlayCircleOutlined, StopOutlined, DeleteOutlined } from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useFleetApi } from '../hooks/useFleetApi';
import { AGV_STATUS_COLORS } from '../../../types/agv';
import type { ISimAgv } from '../../../types/agv';
import { CreateAgvModal } from '../components/CreateAgvModal';

const { Title } = Typography;

export const FleetPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs).sort((a, b) => a.id.localeCompare(b.id))));
  const { fetchFleet, createAgv, updateAgv, deleteAgv, startAgv, stopAgv } = useFleetApi();
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
      sorter: (a, b) => a.id.localeCompare(b.id),
      defaultSortOrder: 'ascend',
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
      title: 'Actions', key: 'actions', align: 'right',
      render: (_, rec) => (
        <Space size="small">
          {rec.isRunning ? (
            <Tooltip title="Stop AGV">
              <Button type="text" danger onClick={() => stopAgv(rec.id)} icon={<StopOutlined />} />
            </Tooltip>
          ) : (
            <Tooltip title="Start AGV">
              <Button type="text" onClick={() => startAgv(rec.id)} icon={<PlayCircleOutlined />} style={{ color: '#52c41a' }} />
            </Tooltip>
          )}
          <Tooltip title="Control">
            <Button type="text" onClick={() => navigate(`/fleet/${rec.id}/control`)} icon={<ControlOutlined />} />
          </Tooltip>
          <Tooltip title="Edit">
            <Button type="text" onClick={() => handleOpenEdit(rec)} icon={<EditOutlined />} />
          </Tooltip>
          <Popconfirm title={`Delete ${rec.id}?`} onConfirm={() => deleteAgv(rec.id)} okText="Delete" okType="danger">
            <Tooltip title="Delete">
              <Button type="text" danger icon={<DeleteOutlined />} />
            </Tooltip>
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
