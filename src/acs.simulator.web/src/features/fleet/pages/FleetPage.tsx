import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Button, Space, Popconfirm, Typography, Tooltip, Table, Input, Select,
  Switch, Dropdown, Modal, Progress, Flex,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import {
  PlusOutlined, EditOutlined, ControlOutlined, PlayCircleOutlined, StopOutlined,
  DeleteOutlined, RobotOutlined, SearchOutlined, MoreOutlined,
  ImportOutlined, ExportOutlined, AppstoreAddOutlined, ClearOutlined,
  VerticalAlignTopOutlined, VerticalAlignBottomOutlined,
} from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { useShallow } from 'zustand/react/shallow';
import { useFleetStore } from '../../../store/fleetStore';
import { useLayoutStore } from '../../../store/layoutStore';
import { useFleetApi } from '../hooks/useFleetApi';
import type { ISimAgv } from '../../../types/agv';
import { SectionCard } from '../../../shared/ui/SectionCard';
import { StatusBadge } from '../../../shared/ui/StatusBadge';
import { exportFleetCsv } from '../../../shared/utils/csv';
import { CreateAgvModal } from '../components/CreateAgvModal';
import { BulkCreateModal } from '../components/BulkCreateModal';
import { ImportAgvModal } from '../components/ImportAgvModal';

const { Text } = Typography;

type StatusFilter = 'all' | 'online' | 'offline' | 'error';

export const FleetPage: React.FC = () => {
  const agvs = useFleetStore(useShallow((s) => Object.values(s.agvs).sort((a, b) => a.id.localeCompare(b.id))));
  const {
    fetchFleet, createAgv, updateAgv, deleteAgv, startAgv, stopAgv, liftAgv, lowerAgv,
    bulkCreate, startMany, stopMany, deleteMany, importAgvs,
  } = useFleetApi();
  const navigate = useNavigate();

  const [modalOpen, setModalOpen] = useState(false);
  const [bulkOpen, setBulkOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [editingAgv, setEditingAgv] = useState<ISimAgv | null>(null);
  const [selectedKeys, setSelectedKeys] = useState<React.Key[]>([]);
  const [searchText, setSearchText] = useState('');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [mapFilter, setMapFilter] = useState<string | undefined>();
  const [busy, setBusy] = useState<string | null>(null);

  useEffect(() => { fetchFleet(); }, [fetchFleet]);

  const runBulk = async (key: string, fn: () => Promise<void>) => {
    setBusy(key);
    try { await fn(); } finally { setBusy(null); setSelectedKeys([]); }
  };

  const mapOptions = useMemo(
    () => [...new Set(agvs.map((a) => a.mapId).filter(Boolean))].sort().map((m) => ({ value: m, label: m })),
    [agvs],
  );

  const filteredAgvs = useMemo(() => {
    const q = searchText.trim().toLowerCase();
    return agvs.filter((a) => {
      const matchSearch = !q || [a.id, a.serialNumber, a.manufacturer]
        .some((v) => String(v ?? '').toLowerCase().includes(q));
      const matchStatus = statusFilter === 'all'
        || (statusFilter === 'online' && a.isConnected)
        || (statusFilter === 'offline' && !a.isConnected)
        || (statusFilter === 'error' && a.status === 'ERROR');
      const matchMap = !mapFilter || a.mapId === mapFilter;
      return matchSearch && matchStatus && matchMap;
    });
  }, [agvs, searchText, statusFilter, mapFilter]);

  // Bỏ chọn các row bị filter ẩn hoặc đã xóa
  useEffect(() => {
    setSelectedKeys((prev) => prev.filter((k) => filteredAgvs.some((a) => a.id === k)));
  }, [filteredAgvs]);

  const selectedAgvs = useMemo(
    () => filteredAgvs.filter((a) => selectedKeys.includes(a.id)),
    [filteredAgvs, selectedKeys],
  );
  const selectedIds = selectedAgvs.map((a) => a.id);
  const canStartSelected = selectedAgvs.some((a) => !a.isRunning);
  const canStopSelected = selectedAgvs.some((a) => a.isRunning);

  const stats = useMemo(() => ({
    total: agvs.length,
    online: agvs.filter((a) => a.isConnected).length,
    driving: agvs.filter((a) => a.status === 'DRIVING').length,
    errors: agvs.filter((a) => a.status === 'ERROR').length,
  }), [agvs]);

  // Stats + fleet actions đẩy lên global header, giải phóng chỗ cho table.
  const setHeaderLeft = useLayoutStore((s) => s.setHeaderLeft);
  const setHeaderRight = useLayoutStore((s) => s.setHeaderRight);
  const filteredRef = useRef(filteredAgvs);
  filteredRef.current = filteredAgvs;

  useEffect(() => {
    setHeaderLeft(
      <Flex align="center" gap={10} style={{ minWidth: 0 }}>
        <Text strong style={{ fontSize: 15, whiteSpace: 'nowrap' }}>Fleet Management</Text>
        <StatusBadge variant="info" text={`${stats.online}/${stats.total} online`} />
        <StatusBadge variant="neutral" text={`${stats.driving} driving`} dot={false} />
        {stats.errors > 0 && <StatusBadge variant="error" text={`${stats.errors} errors`} />}
      </Flex>,
    );
    return () => setHeaderLeft(null);
  }, [setHeaderLeft, stats.online, stats.total, stats.driving, stats.errors]);

  useEffect(() => {
    setHeaderRight(
      <Space size={8}>
        <Button size="small" icon={<PlayCircleOutlined />} loading={busy === 'startAll'}
          onClick={() => runBulk('startAll', () => startMany())}>
          Start All
        </Button>
        <Popconfirm
          title="Stop all AGVs?" description="All running AGVs go offline." okText="Stop All"
          onConfirm={() => runBulk('stopAll', () => stopMany())}
        >
          <Button size="small" danger icon={<StopOutlined />} loading={busy === 'stopAll'}>Stop All</Button>
        </Popconfirm>
        <Dropdown
          trigger={['click']}
          menu={{
            items: [
              { key: 'bulk-create', label: 'Bulk Create…', icon: <AppstoreAddOutlined />, onClick: () => setBulkOpen(true) },
              { key: 'import', label: 'Import CSV…', icon: <ImportOutlined />, onClick: () => setImportOpen(true) },
              { key: 'export', label: 'Export CSV', icon: <ExportOutlined />, onClick: () => exportFleetCsv(filteredRef.current) },
              { type: 'divider' },
              {
                key: 'delete-all', label: 'Delete All…', icon: <DeleteOutlined />, danger: true,
                onClick: () => Modal.confirm({
                  title: 'Delete the entire fleet?',
                  content: `All ${agvs.length} simulated AGVs will be stopped and removed.`,
                  okButtonProps: { danger: true },
                  okText: 'Delete All',
                  onOk: () => deleteMany(),
                }),
              },
            ],
          }}
        >
          <Tooltip title="Fleet actions"><Button size="small" icon={<MoreOutlined />} /></Tooltip>
        </Dropdown>
        <Button size="small" type="primary" icon={<PlusOutlined />}
          onClick={() => { setEditingAgv(null); setModalOpen(true); }}>
          Add AGV
        </Button>
      </Space>,
    );
    return () => setHeaderRight(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setHeaderRight, busy, agvs.length, startMany, stopMany, deleteMany]);

  // Chỉ table body scroll: đo vùng còn lại trừ thead + pagination.
  const tableAreaRef = useRef<HTMLDivElement>(null);
  const [tableY, setTableY] = useState(400);
  useEffect(() => {
    const el = tableAreaRef.current;
    if (!el) return;
    const update = () => setTableY(Math.max(200, el.clientHeight - 104));
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const columns: ColumnsType<ISimAgv> = [
    {
      title: 'AGV', key: 'agv', width: 140,
      sorter: (a, b) => a.id.localeCompare(b.id),
      defaultSortOrder: 'ascend',
      render: (_, rec) => (
        <Space size={10} align="center">
          <Flex align="center" justify="center" style={{
            width: 30, height: 30, borderRadius: 6, flexShrink: 0, fontSize: 14,
            background: 'var(--neutral-bg)', color: 'var(--text-secondary)',
          }}>
            <RobotOutlined />
          </Flex>
          <Button type="link" onClick={() => navigate(`/fleet/${rec.id}/control`)}
            style={{ padding: 0, height: 'auto', fontWeight: 600, fontSize: 13, color: 'var(--text-main)' }}>
            {rec.serialNumber}
          </Button>
        </Space>
      ),
    },
    {
      title: 'Status', dataIndex: 'status', key: 'status', width: 110,
      render: (s: ISimAgv['status'], rec) => (
        <Space direction="vertical" size={2}>
          <StatusBadge status={s} />
          {!rec.isRunning && <Text style={{ fontSize: 10, color: 'var(--text-quaternary)' }}>STOPPED</Text>}
        </Space>
      ),
    },
    {
      title: 'Mode', key: 'mode', width: 120,
      render: (_, rec) => (
        <Space size={4}>
          <StatusBadge
            variant={(rec.operatingMode ?? 'AUTOMATIC').toUpperCase() === 'MANUAL' ? 'warning' : 'neutral'}
            text={rec.operatingMode ?? 'AUTOMATIC'}
            dot={false}
          />
          {rec.holdActions && (
            <Tooltip title="Action hold ON — actions stay WAITING">
              <StatusBadge variant="warning" text="HOLD" dot={false} />
            </Tooltip>
          )}
        </Space>
      ),
    },
    {
      title: 'Position', key: 'position', width: 210,
      render: (_, rec) => (
        <Text style={{ fontSize: 12, fontFamily: 'var(--font-mono, monospace)', whiteSpace: 'nowrap' }}>
          {rec.posX?.toFixed(2)}, {rec.posY?.toFixed(2)} · θ {rec.posTheta?.toFixed(2)}
          <Text style={{ fontSize: 11, color: 'var(--text-tertiary)' }}> · {rec.mapId || '—'}</Text>
        </Text>
      ),
    },
    {
      title: 'Battery', dataIndex: 'batteryLevel', key: 'battery', width: 120,
      sorter: (a, b) => (a.batteryLevel ?? 0) - (b.batteryLevel ?? 0),
      render: (v: number, rec) => (
        <Flex align="center" gap={8}>
          <Progress
            percent={Math.round(v ?? 0)}
            size="small"
            style={{ width: 72, margin: 0 }}
            strokeColor={(v ?? 0) > 50 ? 'var(--success-color)' : (v ?? 0) > 20 ? 'var(--warning-color)' : 'var(--error-color)'}
          />
          {rec.isCharging && <Tooltip title="Charging"><Text style={{ fontSize: 11, color: 'var(--info-color)' }}>⚡</Text></Tooltip>}
        </Flex>
      ),
    },
    {
      title: 'Order', dataIndex: 'orderId', key: 'order', width: 170,
      render: (v: string, rec) => v
        ? (
          <Tooltip title={`${v} #${rec.orderUpdateId}`}>
            <Text style={{ fontSize: 11, fontFamily: 'var(--font-mono, monospace)', display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {v} · u{rec.orderUpdateId}
            </Text>
          </Tooltip>
        )
        : <Text type="secondary" style={{ fontSize: 12 }}>—</Text>,
    },
    {
      title: 'Errors', dataIndex: 'errorCount', key: 'errors', width: 80, align: 'center',
      render: (v: number) => v > 0
        ? <StatusBadge variant="error" text={String(v)} dot={false} />
        : <Text type="secondary">—</Text>,
    },
    {
      title: 'Actions', key: 'actions', width: 170, align: 'right',
      render: (_, rec) => (
        <Space size={4} align="center">
          <Tooltip title={rec.isRunning ? 'Stop' : 'Start'}>
            <Switch
              size="small"
              checked={rec.isRunning}
              loading={busy === `sw_${rec.id}`}
              onChange={(checked) => runBulk(`sw_${rec.id}`, () => checked ? startAgv(rec.id) : stopAgv(rec.id))}
            />
          </Tooltip>
          <Tooltip title="Lift (pick load)">
            <Button
              type="text" size="small"
              icon={<VerticalAlignTopOutlined />}
              disabled={!rec.isRunning}
              loading={busy === `lift_${rec.id}`}
              onClick={() => runBulk(`lift_${rec.id}`, () => liftAgv(rec.id))}
            />
          </Tooltip>
          <Tooltip title="Lower (drop load)">
            <Button
              type="text" size="small"
              icon={<VerticalAlignBottomOutlined />}
              disabled={!rec.isRunning}
              loading={busy === `lower_${rec.id}`}
              onClick={() => runBulk(`lower_${rec.id}`, () => lowerAgv(rec.id))}
            />
          </Tooltip>
          <Tooltip title="Control">
            <Button type="text" size="small" icon={<ControlOutlined />} onClick={() => navigate(`/fleet/${rec.id}/control`)} />
          </Tooltip>
          <Dropdown
            trigger={['click']}
            placement="bottomRight"
            menu={{
              items: [
                { key: 'edit', label: 'Edit', icon: <EditOutlined />, onClick: () => { setEditingAgv(rec); setModalOpen(true); } },
                { key: 'control', label: 'Control', icon: <ControlOutlined />, onClick: () => navigate(`/fleet/${rec.id}/control`) },
                { type: 'divider' },
                {
                  key: 'delete', label: 'Delete', icon: <DeleteOutlined />, danger: true,
                  onClick: () => Modal.confirm({
                    title: `Delete AGV "${rec.serialNumber}"?`,
                    content: 'This stops the simulated AGV and removes it from the fleet.',
                    okButtonProps: { danger: true },
                    okText: 'Delete',
                    onOk: () => deleteAgv(rec.id),
                  }),
                },
              ],
            }}
          >
            <Button type="text" size="small" icon={<MoreOutlined style={{ fontSize: 16 }} />} />
          </Dropdown>
        </Space>
      ),
    },
  ];

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <SectionCard
        noPadding
        style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}
        styles={{ body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' } }}
      >
        {/* Filter bar */}
        <Flex align="center" gap={10} wrap style={{ padding: '10px 16px', borderBottom: '1px solid var(--border-color)', flexShrink: 0 }}>
          <Input
            allowClear
            prefix={<SearchOutlined style={{ color: 'var(--text-quaternary)' }} />}
            placeholder="Search serial / manufacturer…"
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
            style={{ width: 220 }}
          />
          <Select<StatusFilter>
            value={statusFilter}
            onChange={setStatusFilter}
            style={{ width: 120 }}
            options={[
              { value: 'all', label: 'All status' },
              { value: 'online', label: 'Online' },
              { value: 'offline', label: 'Offline' },
              { value: 'error', label: 'Error' },
            ]}
          />
          <Select
            allowClear
            showSearch
            placeholder="Map"
            value={mapFilter}
            onChange={setMapFilter}
            options={mapOptions}
            style={{ width: 160 }}
          />
        </Flex>

        {/* Batch bar khi có selection */}
        {selectedIds.length > 0 && (
          <Flex align="center" gap={10} style={{ padding: '8px 16px', background: 'var(--primary-bg)', borderBottom: '1px solid var(--primary-border)', flexShrink: 0 }}>
            <Text strong style={{ fontSize: 13, color: 'var(--primary-color)' }}>{selectedIds.length} selected</Text>
            <Button
              size="small"
              icon={<PlayCircleOutlined />}
              disabled={!canStartSelected}
              loading={busy === 'startSel'}
              onClick={() => runBulk('startSel', () => startMany(selectedIds))}
            >
              Start
            </Button>
            <Button
              size="small"
              danger
              icon={<StopOutlined />}
              disabled={!canStopSelected}
              loading={busy === 'stopSel'}
              onClick={() => runBulk('stopSel', () => stopMany(selectedIds))}
            >
              Stop
            </Button>
            <Popconfirm
              title={`Delete ${selectedIds.length} AGV(s)?`}
              okText="Delete"
              okButtonProps={{ danger: true }}
              onConfirm={() => runBulk('deleteSel', () => deleteMany(selectedIds))}
            >
              <Button size="small" danger type="text" icon={<DeleteOutlined />} loading={busy === 'deleteSel'}>Delete</Button>
            </Popconfirm>
            <Button size="small" type="text" icon={<ClearOutlined />} onClick={() => setSelectedKeys([])}>Clear</Button>
          </Flex>
        )}

        <div ref={tableAreaRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Table
            dataSource={filteredAgvs}
            columns={columns}
            rowKey="id"
            size="middle"
            scroll={{ y: tableY }}
            rowSelection={{
              selectedRowKeys: selectedKeys,
              onChange: setSelectedKeys,
            }}
            rowClassName={(r) => r.status === 'ERROR' ? 'row-error' : !r.isRunning ? 'row-offline' : ''}
            pagination={{
              pageSize: 20,
              showSizeChanger: true,
              pageSizeOptions: ['10', '20', '50', '100'],
              showTotal: (total) => `Total: ${total} AGVs`,
            }}
          />
        </div>
      </SectionCard>

      <CreateAgvModal
        open={modalOpen}
        onClose={() => { setModalOpen(false); setEditingAgv(null); }}
        onCreate={createAgv}
        onUpdate={updateAgv}
        editingAgv={editingAgv}
      />
      <BulkCreateModal open={bulkOpen} onClose={() => setBulkOpen(false)} onSubmit={bulkCreate} />
      <ImportAgvModal open={importOpen} onClose={() => setImportOpen(false)} onImport={importAgvs} />
    </div>
  );
};
