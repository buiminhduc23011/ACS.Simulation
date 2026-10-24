import type { IAgvMapMapping, ICreateAgvRequest, ISimAgv } from '../../types/agv';

export type CsvCell = string | number | boolean | null | undefined;

const escapeCsvValue = (value: CsvCell) => `"${String(value ?? '').replace(/"/g, '""')}"`;

/** Xuất CSV với BOM UTF-8 để Excel đọc đúng tiếng Việt. */
export const exportCsv = (fileName: string, headers: CsvCell[], rows: CsvCell[][]) => {
  const csvContent = [headers, ...rows].map((row) => row.map(escapeCsvValue).join(',')).join('\r\n');
  const blob = new Blob(['﻿' + csvContent], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
};

/** Parse CSV text — hỗ trợ cell có dấu ngoặc kép, newline trong cell. */
export const parseCsvText = (text: string): string[][] => {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let inQuotes = false;
  const src = text.charCodeAt(0) === 0xFEFF ? text.slice(1) : text;

  for (let i = 0; i < src.length; i++) {
    const ch = src[i];
    if (inQuotes) {
      if (ch === '"') {
        if (src[i + 1] === '"') { cell += '"'; i++; }
        else inQuotes = false;
      } else cell += ch;
    } else if (ch === '"') {
      inQuotes = true;
    } else if (ch === ',') {
      row.push(cell); cell = '';
    } else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && src[i + 1] === '\n') i++;
      row.push(cell); cell = '';
      if (row.some((c) => c !== '')) rows.push(row);
      row = [];
    } else cell += ch;
  }
  row.push(cell);
  if (row.some((c) => c !== '')) rows.push(row);
  return rows;
};

// ─── Fleet CSV schema ────────────────────────────────────────────────────

export const AGV_CSV_HEADERS = [
  'serialNumber', 'manufacturer', 'posX', 'posY', 'posTheta', 'mapId',
  'ipAddress', 'macAddress', 'vehicleShapeId', 'navigationTechnologyId', 'mapMappings',
] as const;

/** mapMappings encode: "acsMap:simMap;acsMap2:simMap2" */
const encodeMappings = (mappings?: IAgvMapMapping[]) =>
  (mappings ?? []).map((m) => `${m.sourceMapId}:${m.targetMapId}`).join(';');

const decodeMappings = (raw: string): IAgvMapMapping[] =>
  raw.split(';')
    .map((pair) => pair.trim())
    .filter(Boolean)
    .map((pair) => {
      const idx = pair.indexOf(':');
      return idx < 0
        ? { sourceMapId: pair, targetMapId: pair }
        : { sourceMapId: pair.slice(0, idx).trim(), targetMapId: pair.slice(idx + 1).trim() };
    })
    .filter((m) => m.sourceMapId && m.targetMapId);

export const agvToCsvRow = (a: ISimAgv): CsvCell[] => [
  a.serialNumber, a.manufacturer, a.posX, a.posY, a.posTheta, a.mapId,
  a.ipAddress, a.macAddress, a.vehicleShapeId ?? 1, a.navigationTechnologyId ?? 1,
  encodeMappings(a.mapMappings),
];

export const exportFleetCsv = (agvs: ISimAgv[]) =>
  exportCsv(
    `fleet-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.csv`,
    [...AGV_CSV_HEADERS],
    agvs.map(agvToCsvRow),
  );

export const downloadFleetCsvTemplate = () =>
  exportCsv('fleet-import-template.csv', [...AGV_CSV_HEADERS], [
    ['AGV-001', 'stivn', 0, 0, 0, 'FLOOR_1', '192.168.1.101', '', 1, 1, ''],
    ['AGV-002', 'stivn', 3, 0, 0, 'FLOOR_1', '192.168.1.102', '', 1, 1, 'ACS_MAP:FLOOR_1'],
  ]);

export interface IParsedAgvCsv {
  agvs: ICreateAgvRequest[];
  errors: { row: number; message: string }[];
}

/** Parse CSV thành create requests; validate từng dòng, dòng lỗi trả trong errors. */
export const parseAgvCsv = (text: string): IParsedAgvCsv => {
  const rows = parseCsvText(text);
  const result: IParsedAgvCsv = { agvs: [], errors: [] };
  if (rows.length === 0) {
    result.errors.push({ row: 0, message: 'File is empty' });
    return result;
  }

  const header = rows[0].map((h) => h.trim().toLowerCase());
  const col = (name: string) => header.indexOf(name.toLowerCase());
  if (col('serialNumber') < 0 || col('manufacturer') < 0) {
    result.errors.push({ row: 1, message: 'Missing required columns: serialNumber, manufacturer' });
    return result;
  }

  const num = (v: string | undefined, fallback = 0) => {
    const n = Number(v);
    return v === undefined || v.trim() === '' ? fallback : n;
  };

  rows.slice(1).forEach((cells, i) => {
    const rowNo = i + 2;
    const get = (name: string) => (col(name) >= 0 ? (cells[col(name)] ?? '').trim() : '');
    const serialNumber = get('serialNumber');
    const manufacturer = get('manufacturer');

    if (!serialNumber) { result.errors.push({ row: rowNo, message: 'serialNumber is required' }); return; }
    if (!manufacturer) { result.errors.push({ row: rowNo, message: 'manufacturer is required' }); return; }
    if (['posX', 'posY', 'posTheta'].some((k) => get(k) !== '' && Number.isNaN(Number(get(k))))) {
      result.errors.push({ row: rowNo, message: 'posX/posY/posTheta must be numbers' });
      return;
    }

    result.agvs.push({
      serialNumber,
      manufacturer,
      posX: num(get('posX')),
      posY: num(get('posY')),
      posTheta: num(get('posTheta')),
      mapId: get('mapId'),
      ipAddress: get('ipAddress') || undefined,
      macAddress: get('macAddress') || undefined,
      vehicleShapeId: num(get('vehicleShapeId'), 1),
      navigationTechnologyId: num(get('navigationTechnologyId'), 1),
      mapMappings: decodeMappings(get('mapMappings')),
    });
  });

  if (rows.length === 1) {
    result.errors.push({ row: 2, message: 'No data rows' });
  }
  return result;
};
