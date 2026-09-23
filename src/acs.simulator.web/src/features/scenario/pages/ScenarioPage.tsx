import React, { useEffect, useRef, useState } from 'react';
import {
  Row, Col, Card, List, Button, Tag, Progress, Typography,
  Upload, message, Divider, Timeline, Space, Badge, Empty
} from 'antd';
import { UploadOutlined, PlayCircleOutlined, StopOutlined } from '@ant-design/icons';
import type { UploadProps } from 'antd';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import { useScenarioStore } from '../../../store/scenarioStore';
import { useSimulatorSignalR } from '../../../infrastructure/signalr/useSimulatorSignalR';
import type { IScenario } from '../../../types/scenario';

const { Title, Text } = Typography;

const STATUS_COLORS: Record<string, string> = {
  idle: 'default', running: 'processing', completed: 'success', failed: 'error', stopped: 'warning',
};

export const ScenarioPage: React.FC = () => {
  const { scenarios, currentRun, logs, setScenarios, setCurrentRun, appendStepResult, appendLog, clearLogs } = useScenarioStore();
  const [selected, setSelected] = useState<IScenario | null>(null);
  const [loading, setLoading] = useState(false);
  const logEndRef = useRef<HTMLDivElement>(null);

  useSimulatorSignalR(['scenario'], {
    onScenarioStarted: (run) => { setCurrentRun(run); clearLogs(); },
    onScenarioStep: (result) => appendStepResult(result),
    onScenarioProgress: (run) => setCurrentRun(run),
    onScenarioFinished: (run) => { setCurrentRun(run); message.success(`Scenario ${run.scenarioName} ${run.status}`); },
    onLogMessage: (log) => appendLog(log),
  });

  useEffect(() => {
    fetchScenarios();
    apiClient.get(ENDPOINTS.scenarios.currentRun).then((r) => setCurrentRun(r.data)).catch(() => { });
  }, []);

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [logs]);

  const fetchScenarios = async () => {
    const r = await apiClient.get<IScenario[]>(ENDPOINTS.scenarios.list);
    setScenarios(r.data);
  };

  const runScenario = async (id: string) => {
    setLoading(true);
    try {
      await apiClient.post(ENDPOINTS.scenarios.run(id));
    } catch { message.error('Failed to start scenario'); }
    finally { setLoading(false); }
  };

  const stopScenario = async () => {
    await apiClient.post(ENDPOINTS.scenarios.stop);
    message.warning('Scenario stopped');
  };

  const uploadProps: UploadProps = {
    accept: '.json',
    showUploadList: false,
    customRequest: async ({ file, onSuccess, onError }) => {
      const fd = new FormData();
      fd.append('file', file as File);
      try {
        await apiClient.post(ENDPOINTS.scenarios.import, fd, { headers: { 'Content-Type': 'multipart/form-data' } });
        message.success('Scenario imported');
        await fetchScenarios();
        onSuccess?.('ok');
      } catch (e) { onError?.(e as Error); }
    },
  };

  const progress = currentRun && currentRun.totalSteps > 0
    ? Math.round((currentRun.currentStep / currentRun.totalSteps) * 100)
    : 0;

  return (
    <div>
      <Title level={4} style={{ marginBottom: 16 }}>Scenario Runner</Title>
      <Row gutter={[16, 16]}>
        {/* Left: Scenario List */}
        <Col xs={24} lg={8}>
          <Card
            title="Scenarios"
            extra={
              <Upload {...uploadProps}>
                <Button icon={<UploadOutlined />} size="small">Import</Button>
              </Upload>
            }
          >
            <List
              dataSource={scenarios}
              locale={{ emptyText: <Empty description="No scenarios. Import a JSON file." /> }}
              renderItem={(s) => (
                <List.Item
                  style={{ cursor: 'pointer', background: selected?.id === s.id ? '#1d2333' : 'transparent', padding: '8px 12px', borderRadius: 4 }}
                  onClick={() => setSelected(s)}
                  actions={[
                    <Button
                      key="run"
                      type="primary"
                      size="small"
                      icon={<PlayCircleOutlined />}
                      loading={loading}
                      disabled={currentRun?.status === 'running'}
                      onClick={(e) => { e.stopPropagation(); runScenario(s.id); }}
                    >
                      Run
                    </Button>,
                  ]}
                >
                  <List.Item.Meta
                    title={<Text style={{ fontSize: 13 }}>{s.name}</Text>}
                    description={<Text type="secondary" style={{ fontSize: 11 }}>{s.steps?.length ?? 0} steps{s.description ? ` · ${s.description}` : ''}</Text>}
                  />
                </List.Item>
              )}
            />
          </Card>
        </Col>

        {/* Right: Run Status + Logs */}
        <Col xs={24} lg={16}>
          {/* Current Run */}
          {currentRun && (
            <Card
              title={
                <Space>
                  <Text>Running: <Text strong>{currentRun.scenarioName}</Text></Text>
                  <Badge status={STATUS_COLORS[currentRun.status] as never} text={currentRun.status.toUpperCase()} />
                </Space>
              }
              extra={
                currentRun.status === 'running' && (
                  <Button danger icon={<StopOutlined />} size="small" onClick={stopScenario}>Stop</Button>
                )
              }
              style={{ marginBottom: 16 }}
            >
              <Progress percent={progress} status={currentRun.status === 'failed' ? 'exception' : currentRun.status === 'completed' ? 'success' : 'active'} />
              <Text type="secondary">{currentRun.currentStep} / {currentRun.totalSteps} steps</Text>

              {currentRun.stepResults && currentRun.stepResults.length > 0 && (
                <>
                  <Divider style={{ margin: '12px 0' }} />
                  <Timeline
                    items={currentRun.stepResults.slice(-10).map((r, i) => ({
                      key: i,
                      color: r.success ? 'green' : 'red',
                      children: (
                        <div style={{ fontSize: 12 }}>
                          <Tag style={{ fontSize: 10 }}>{r.stepType}</Tag>
                          <span style={{ color: r.success ? '#52c41a' : '#ff4d4f' }}>
                            {r.success ? 'OK' : 'FAIL'}
                          </span>
                          {r.message && <span> — {r.message}</span>}
                          <span style={{ color: '#888', marginLeft: 8 }}>{r.durationMs}ms</span>
                        </div>
                      ),
                    }))}
                  />
                </>
              )}
            </Card>
          )}

          {/* Log Console */}
          <Card
            title="Log Console"
            extra={<Button size="small" onClick={clearLogs}>Clear</Button>}
            bodyStyle={{ padding: 0 }}
          >
            <div style={{ background: '#0d1117', height: 320, overflowY: 'auto', padding: '8px 12px', fontFamily: 'monospace', fontSize: 12 }}>
              {logs.length === 0 && <Text type="secondary">No logs yet...</Text>}
              {logs.map((log, i) => {
                const colors: Record<string, string> = { Info: '#58a6ff', Warning: '#d29922', Error: '#f85149', Debug: '#8b949e' };
                return (
                  <div key={i} style={{ color: colors[log.level] ?? '#c9d1d9', marginBottom: 2 }}>
                    <span style={{ color: '#8b949e' }}>[{new Date(log.timestamp).toLocaleTimeString()}]</span>
                    {' '}
                    <span style={{ color: '#79c0ff' }}>[{log.agvId}]</span>
                    {' '}{log.message}
                  </div>
                );
              })}
              <div ref={logEndRef} />
            </div>
          </Card>
        </Col>
      </Row>
    </div>
  );
};
