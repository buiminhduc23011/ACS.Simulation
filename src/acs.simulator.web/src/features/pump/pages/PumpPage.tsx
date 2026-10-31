import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Button, Flex, Form, Input, InputNumber, Popconfirm, Progress, Space, Switch,
  Table, Tabs, Tooltip, Typography, message,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import {
  ThunderboltOutlined, ApiOutlined, ReloadOutlined, SearchOutlined,
  StopOutlined, CheckCircleFilled, CloseCircleFilled,
} from '@ant-design/icons';
import { PageHeader } from '../../../shared/ui/PageHeader';
import { SectionCard } from '../../../shared/ui/SectionCard';
import { StatusBadge } from '../../../shared/ui/StatusBadge';
import { usePumpApi } from '../hooks/usePumpApi';
import type { IPumpSnapshot, IWorkflowRuntime, IWorkflowSummary } from '../../../types/pump';

const { Text } = Typography;
const POLL_MS = 3000;

export const PumpPage: React.FC = () => {
  const { fetchState, saveConfig, setEnabled, loadWorkflows, cancelTransaction } = usePumpApi();
  const [state, setState] = useState<IPumpSnapshot | null>(null);
  const [workflows, setWorkflows] = useState<IWorkflowSummary[]>([]);
  const [wfSearch, setWfSearch] = useState('');
  const [selectedWfIds, setSelectedWfIds] = useState<number[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [cfgForm] = Form.useForm();
  const serverOffsetRef = useRef(0);
  const [, forceTick] = useState(0);

  const refresh = useCallback(async () => {
    const snap = await fetchState();
    if (!snap) return;
    serverOffsetRef.current = new Date(snap.serverTimeUtc).getTime() - Date.now();
    setState(snap);
    if (document.activeElement?.tagName !== 'INPUT') {
      setSelectedWfIds(snap.workflowIds);
    }
  }, [fetchState]);

  useEffect(() => {
    refresh();
    const poll = setInterval(refresh, POLL_MS);
    const clock = setInterval(() => forceTick((t) => t + 1), 500);
    return () => { clearInterval(poll); clearInterval(clock); };
  }, [refresh]);

  const wfName = useMemo(() => {
    const map = new Map(workflows.map((w) => [w.id, w.name]));
    return (id: number) => map.get(id) ?? '';
  }, [workflows]);

  const doToggle = async (enabled: boolean) => {
    setBusy('toggle');
    try { setState(await setEnabled(enabled)); }
    catch { message.error('Pump toggle failed'); }
    finally { setBusy(null); }
  };

  const doSaveConfig = async () => {
    const values = await cfgForm.validateFields();
    setBusy('cfg');
    try {
      const snap = await saveConfig({
        baseUrl: (values.baseUrl ?? '').trim() || undefined,
        apiKey: (values.apiKey ?? '').trim() || undefined,
        intervalSeconds: values.intervalSeconds,
        targetRunning: values.targetRunning,
      });
      setState(snap);
      cfgForm.setFieldValue('apiKey', '');
      message.success('Pump config saved');
    } catch { message.error('Save failed'); }
    finally { setBusy(null); }
  };

  const doLoadWorkflows = async () => {
    setBusy('loadWf');
    try {
      setWorkflows(await loadWorkflows());
      message.success('Workflows loaded from ACS');
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      message.error(msg ?? 'Could not reach ACS');
    } finally { setBusy(null); }
  };

  const doSaveWfIds = async (ids: number[]) => {
    setSelectedWfIds(ids);
    try { setState(await saveConfig({ workflowIds: ids })); }
    catch { message.error('Save workflow list failed'); }
  };

  const doCancelTx = async (txId: number) => {
    setBusy(`cancel_${txId}`);
    try { await cancelTransaction(txId); await refresh(); }
    catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      message.error(msg ?? 'Cancel failed');
    } finally { setBusy(null); }
  };

  // Countdown tới tick kế — dùng offset server; tính lại mỗi render (forceTick 500ms).
  const nextTick = (() => {
    if (!state?.enabled || !state.nextTickAtUtc) return null;
    const remainMs = new Date(state.nextTickAtUtc).getTime() - (Date.now() + serverOffsetRef.current);
    return { remainSec: Math.max(0, Math.ceil(remainMs / 1000)), pct: Math.min(100, Math.max(0, (1 - remainMs / (state.intervalSeconds * 1000)) * 100)) };
  })();

  const totalStarts = state?.runtimes.reduce((s, r) => s + r.startedCount, 0) ?? 0;

  const statePill = (r: IWorkflowRuntime) => {
    if (r.lastState === 'Running') return <StatusBadge variant="info" text="Running" />;
    if (r.lastState === 'Pending' || r.lastState === 'Created') return <StatusBadge variant="warning" text={r.lastState} />;
    if (r.lastState === 'Completed') return <StatusBadge variant="success" text="Completed" />;
    if (r.lastState === 'Failed' || r.lastState === 'Cancelled' || r.lastState === 'Timeout')
      return <StatusBadge variant="error" text={r.lastState} />;
    if (r.lastState) return <StatusBadge variant="neutral" text={r.lastState} dot={false} />;
    if (r.note) return <StatusBadge variant="error" text="error" />;
    return <Text type="secondary">—</Text>;
  };

  const rtColumns: ColumnsType<IWorkflowRuntime> = [
    {
      title: 'Workflow', key: 'wf', width: 220,
      render: (_, r) => (
        <Space direction="vertical" size={0}>
          <Text strong style={{ fontSize: 13 }}>#{r.workflowId} {wfName(r.workflowId)}</Text>
          <Text style={{ fontSize: 11, fontFamily: 'var(--font-mono, monospace)', color: 'var(--text-quaternary)' }}>
            tx {r.lastTransactionId || '—'}
          </Text>
        </Space>
      ),
    },
    { title: 'State', key: 'state', width: 110, render: (_, r) => statePill(r) },
    {
      title: 'AGV', key: 'agv', width: 110,
      render: (_, r) => r.agvName
        ? <Text style={{ fontSize: 12 }}>{r.agvName}{r.commandAccepted ? ' ✓' : ''}</Text>
        : <Text type="secondary">—</Text>,
    },
    { title: 'Starts', dataIndex: 'startedCount', key: 'starts', width: 85, align: 'center',
      render: (v: number) => <Text strong>{v}</Text>, sorter: (a, b) => a.startedCount - b.startedCount },
    {
      title: 'Note / Pending', key: 'note', ellipsis: true,
      render: (_, r) => (
        <Tooltip title={r.note || r.pendingReason || ''}>
          <Text type="secondary" style={{ fontSize: 12 }}>{r.note || r.pendingReason || '—'}</Text>
        </Tooltip>
      ),
    },
    {
      title: 'Checked', dataIndex: 'lastCheckedAt', key: 'checked', width: 100,
      render: (v?: string | null) => v
        ? <Text type="secondary" style={{ fontSize: 11 }}>{new Date(v).toLocaleTimeString()}</Text>
        : <Text type="secondary">—</Text>,
    },
    {
      title: '', key: 'actions', width: 56, align: 'right',
      render: (_, r) => {
        const busyTx = r.lastState === 'Pending' || r.lastState === 'Running' || r.lastState === 'Created';
        if (!busyTx || !r.lastTransactionId) return null;
        return (
          <Popconfirm
            title={`Cancel tx ${r.lastTransactionId}?`}
            description="ACS sẽ hủy transaction đang chạy của workflow này."
            okText="Cancel tx" okButtonProps={{ danger: true }}
            onConfirm={() => doCancelTx(r.lastTransactionId)}
          >
            <Button type="text" size="small" danger icon={<StopOutlined />} loading={busy === `cancel_${r.lastTransactionId}`} />
          </Popconfirm>
        );
      },
    },
  ];

  const wfColumns: ColumnsType<IWorkflowSummary> = [
    { title: 'ID', dataIndex: 'id', width: 90, sorter: (a, b) => a.id - b.id,
      render: (v: number) => <Text strong style={{ fontFamily: 'var(--font-mono, monospace)' }}>#{v}</Text> },
    { title: 'Name', dataIndex: 'name', ellipsis: true },
  ];

  const filteredWfs = useMemo(() => {
    const q = wfSearch.trim().toLowerCase();
    return q ? workflows.filter((w) => String(w.id).includes(q) || w.name.toLowerCase().includes(q)) : workflows;
  }, [workflows, wfSearch]);

  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <PageHeader
        icon={<ThunderboltOutlined />}
        title="Workflow Pump"
        subtitle="Giữ đúng số transaction running mục tiêu trên ACS"
        stats={[
          { title: 'Running', value: `${state?.runningCount ?? 0}/${state?.targetRunning ?? 0}` },
          { title: 'Workflows', value: state?.workflowIds.length ?? 0 },
          { title: 'Total starts', value: totalStarts },
        ]}
        actions={
          <Flex align="center" gap={10}>
            {state?.acsReachable
              ? <StatusBadge variant="success" text={`ACS online${state.acsVersion ? ` · v${state.acsVersion}` : ''}`} />
              : <StatusBadge variant="error" text="ACS offline" />}
            <Switch
              checked={state?.enabled ?? false}
              loading={busy === 'toggle'}
              onChange={doToggle}
              checkedChildren="ON" unCheckedChildren="OFF"
            />
          </Flex>
        }
      />

      <Tabs
        style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}
        items={[
          {
            key: 'monitor',
            label: 'Monitor',
            children: (
              <Flex vertical gap={12} style={{ paddingBottom: 16 }}>
                <SectionCard title="Pump control" uiVariant="compact">
                  <Flex align="center" gap={16} wrap>
                    <Button
                      type={state?.enabled ? 'default' : 'primary'}
                      danger={state?.enabled}
                      icon={<ThunderboltOutlined />}
                      loading={busy === 'toggle'}
                      onClick={() => doToggle(!(state?.enabled ?? false))}
                      style={{ minWidth: 140 }}
                    >
                      {state?.enabled ? 'TẮT PUMP' : 'BẬT PUMP'}
                    </Button>
                    <Flex vertical flex={1} style={{ minWidth: 240 }}>
                      <Text style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
                        {state?.enabled
                          ? nextTick
                            ? `Tick tiếp theo sau ${nextTick.remainSec}s — mỗi tick check toàn bộ workflow, start bù đúng 1 workflow (xoay vòng)`
                            : 'Đang lên lịch…'
                          : `Pump tắt — khi bật sẽ giữ ${state?.targetRunning ?? 0} transaction running, mỗi ${state?.intervalSeconds ?? 0}s một tick`}
                      </Text>
                      <Progress
                        percent={state?.enabled ? nextTick?.pct ?? 0 : 0}
                        showInfo={false}
                        size="small"
                        strokeColor={{ from: '#2563EB', to: '#22D3EE' }}
                        style={{ margin: '6px 0 0' }}
                      />
                    </Flex>
                  </Flex>
                </SectionCard>

                <SectionCard
                  title={`Pumped workflows${state?.workflowIds.length ? ` (${state.workflowIds.length})` : ''}`}
                  noPadding
                >
                  <Table<IWorkflowRuntime>
                    dataSource={state?.runtimes ?? []}
                    columns={rtColumns}
                    rowKey="workflowId"
                    size="small"
                    pagination={{
                      pageSize: 15,
                      showSizeChanger: true,
                      pageSizeOptions: ['10', '15', '20', '50'],
                      showTotal: (total) => `Total: ${total} workflows`,
                    }}
                    locale={{ emptyText: 'Chưa chọn workflow — sang tab Settings để chọn' }}
                  />
                </SectionCard>

                <SectionCard title="Event log" uiVariant="compact">
                  <Flex vertical gap={4} style={{ maxHeight: 220, overflowY: 'auto', fontFamily: 'var(--font-mono, monospace)', fontSize: 12 }}>
                    {(state?.events.length ?? 0) === 0 && <Text type="secondary">—</Text>}
                    {state?.events.map((e, i) => (
                      <div key={i} style={{
                        padding: '4px 10px',
                        borderLeft: '2px solid var(--border-color)',
                        background: 'var(--neutral-bg, rgba(0,0,0,0.02))',
                        borderRadius: '0 6px 6px 0',
                      }}>{e}</div>
                    ))}
                  </Flex>
                </SectionCard>
              </Flex>
            ),
          },
          {
            key: 'settings',
            label: 'Settings',
            children: (
              <Flex vertical gap={12} style={{ paddingBottom: 16, maxWidth: 1100 }}>
                <SectionCard title={<><ApiOutlined /> ACS connection</>} uiVariant="compact">
                  <Form form={cfgForm} layout="vertical" key={state?.baseUrl ?? 'x'}
                    initialValues={{
                      baseUrl: state?.baseUrl ?? '',
                      intervalSeconds: state?.intervalSeconds ?? 10,
                      targetRunning: state?.targetRunning ?? 5,
                    }}
                  >
                    <Flex gap={12} wrap>
                      <Form.Item name="baseUrl" label="ACS Base URL" style={{ flex: 2, minWidth: 260, marginBottom: 12 }}>
                        <Input placeholder={state?.baseUrl ?? 'http://127.0.0.1:9051'} />
                      </Form.Item>
                      <Form.Item name="apiKey" label="API Key (X-ACS-API-Key)" style={{ flex: 2, minWidth: 260, marginBottom: 12 }}>
                        <Input.Password placeholder={state?.hasApiKey ? '(key đã lưu — nhập để thay)' : 'nhập key'} autoComplete="off" />
                      </Form.Item>
                      <Form.Item name="intervalSeconds" label="Interval (giây, ≥3)" style={{ width: 140, marginBottom: 12 }}>
                        <InputNumber min={3} max={3600} style={{ width: '100%' }} />
                      </Form.Item>
                      <Form.Item name="targetRunning" label="Số running mục tiêu" style={{ width: 160, marginBottom: 12 }}>
                        <InputNumber min={0} max={1000} style={{ width: '100%' }} />
                      </Form.Item>
                    </Flex>
                  </Form>
                  <Space>
                    <Button type="primary" onClick={doSaveConfig} loading={busy === 'cfg'}>Lưu cấu hình</Button>
                    <Button icon={<ReloadOutlined />} onClick={doLoadWorkflows} loading={busy === 'loadWf'}>Tải workflow từ ACS</Button>
                  </Space>
                  <Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 10 }}>
                    Base URL để trống = dùng ACS API URL trong Settings chung của simulator.
                  </Text>
                </SectionCard>

                <SectionCard
                  title="Workflow cần pump"
                  noPadding
                  extra={
                    <Space size={8}>
                      <Input
                        allowClear size="small" prefix={<SearchOutlined />}
                        placeholder="Lọc id / tên…" value={wfSearch}
                        onChange={(e) => setWfSearch(e.target.value)} style={{ width: 200 }}
                      />
                      <Button size="small" onClick={() => doSaveWfIds(workflows.map((w) => w.id))}>Chọn hết</Button>
                      <Button size="small" onClick={() => doSaveWfIds([])}>Bỏ hết</Button>
                    </Space>
                  }
                >
                  <Table<IWorkflowSummary>
                    dataSource={filteredWfs}
                    columns={wfColumns}
                    rowKey="id"
                    size="small"
                    rowSelection={{
                      selectedRowKeys: selectedWfIds,
                      onChange: (keys) => doSaveWfIds(keys.map(Number)),
                      preserveSelectedRowKeys: true,
                    }}
                    pagination={{
                      pageSize: 15,
                      showSizeChanger: true,
                      pageSizeOptions: ['10', '15', '20', '50'],
                      showTotal: (total) => `Total: ${total} workflows`,
                    }}
                    locale={{ emptyText: workflows.length === 0 ? 'Bấm "Tải workflow từ ACS" để lấy danh sách' : 'Không khớp bộ lọc' }}
                  />
                </SectionCard>
              </Flex>
            ),
          },
        ]}
      />
    </div>
  );
};
