import React, { useState } from 'react';
import { Modal, Upload, Alert, Table, Button, Typography, Space, Progress } from 'antd';
import { InboxOutlined, DownloadOutlined } from '@ant-design/icons';
import type { ICreateAgvRequest } from '../../../types/agv';
import { downloadFleetCsvTemplate, parseAgvCsv } from '../../../shared/utils/csv';

const { Text, Paragraph } = Typography;

interface Props {
  open: boolean;
  onClose: () => void;
  onImport: (reqs: ICreateAgvRequest[]) => Promise<{ created: string[]; failed: { serial: string; message: string }[] }>;
}

interface ParsedRow extends ICreateAgvRequest {
  key: number;
}

/** Import fleet từ CSV: upload → preview → tạo lần lượt; báo kết quả từng dòng. */
export const ImportAgvModal: React.FC<Props> = ({ open, onClose, onImport }) => {
  const [rows, setRows] = useState<ParsedRow[]>([]);
  const [parseErrors, setParseErrors] = useState<{ row: number; message: string }[]>([]);
  const [fileName, setFileName] = useState('');
  const [importing, setImporting] = useState(false);
  const [summary, setSummary] = useState<{ created: number; failed: { serial: string; message: string }[] } | null>(null);

  const reset = () => {
    setRows([]);
    setParseErrors([]);
    setFileName('');
    setSummary(null);
  };

  const handleClose = () => { reset(); onClose(); };

  const handleFile = async (file: File) => {
    const text = await file.text();
    const parsed = parseAgvCsv(text);
    setFileName(file.name);
    setRows(parsed.agvs.map((a, i) => ({ ...a, key: i })));
    setParseErrors(parsed.errors);
    setSummary(null);
    return false; // chặn auto-upload của antd
  };

  const handleImport = async () => {
    setImporting(true);
    try {
      const res = await onImport(rows);
      setSummary({ created: res.created.length, failed: res.failed });
      if (res.failed.length === 0) handleClose();
    } finally {
      setImporting(false);
    }
  };

  return (
    <Modal
      title="Import AGVs from CSV"
      open={open}
      onCancel={handleClose}
      width={720}
      footer={[
        <Button key="close" onClick={handleClose}>Close</Button>,
        <Button
          key="import"
          type="primary"
          disabled={rows.length === 0}
          loading={importing}
          onClick={handleImport}
        >
          Import {rows.length > 0 ? `${rows.length} AGV${rows.length !== 1 ? 's' : ''}` : ''}
        </Button>,
      ]}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Upload.Dragger
          accept=".csv"
          maxCount={1}
          showUploadList={false}
          beforeUpload={(file) => { void handleFile(file); return false; }}
        >
          <p className="ant-upload-drag-icon"><InboxOutlined /></p>
          <p className="ant-upload-text">Click or drag a .csv file here</p>
          <p className="ant-upload-hint">
            Required columns: serialNumber, manufacturer. Optional: posX, posY, posTheta, mapId,
            ipAddress, macAddress, vehicleShapeId, navigationTechnologyId, mapMappings (acsMap:simMap;…)
          </p>
        </Upload.Dragger>

        <Button size="small" icon={<DownloadOutlined />} onClick={downloadFleetCsvTemplate}>
          Download CSV template
        </Button>

        {summary && (
          <Alert
            type={summary.failed.length === 0 ? 'success' : 'warning'}
            showIcon
            message={`Imported ${summary.created} AGV(s)` + (summary.failed.length ? `, ${summary.failed.length} failed` : '')}
            description={summary.failed.length > 0 && (
              <Text style={{ fontSize: 12 }}>{summary.failed.map((f) => `${f.serial}: ${f.message}`).join(' · ')}</Text>
            )}
          />
        )}

        {parseErrors.length > 0 && (
          <Alert
            type="error"
            showIcon
            message={`${parseErrors.length} row(s) have errors`}
            description={
              <div style={{ maxHeight: 120, overflowY: 'auto' }}>
                {parseErrors.map((e, i) => <div key={i}><Text style={{ fontSize: 12 }}>Row {e.row}: {e.message}</Text></div>)}
              </div>
            }
          />
        )}

        {fileName && !summary && (
          <Paragraph type="secondary" style={{ margin: 0, fontSize: 12 }}>
            {fileName} — {rows.length} valid row{rows.length !== 1 ? 's' : ''}
          </Paragraph>
        )}

        {rows.length > 0 && (
          <Table
            dataSource={rows}
            size="small"
            pagination={rows.length > 8 ? { pageSize: 8 } : false}
            scroll={{ x: true }}
            columns={[
              { title: 'Serial', dataIndex: 'serialNumber', width: 130 },
              { title: 'Manufacturer', dataIndex: 'manufacturer', width: 120 },
              { title: 'X', dataIndex: 'posX', width: 70 },
              { title: 'Y', dataIndex: 'posY', width: 70 },
              { title: 'Theta', dataIndex: 'posTheta', width: 70 },
              { title: 'Map', dataIndex: 'mapId', width: 110, render: (v: string) => v || '—' },
              {
                title: 'Mappings', dataIndex: 'mapMappings', width: 130,
                render: (m: { sourceMapId: string; targetMapId: string }[]) =>
                  m?.length ? m.map((x) => `${x.sourceMapId}→${x.targetMapId}`).join(', ') : '—',
              },
            ]}
          />
        )}
      </Space>
    </Modal>
  );
};
