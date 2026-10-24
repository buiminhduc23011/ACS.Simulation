import { useCallback } from 'react';
import { message } from 'antd';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import type {
  ISimAgv, ICreateAgvRequest, IInboundMqttMessage, ISimMapInfo, ISimMapNode,
  IBulkCreateRequest, IBulkCreateResult,
} from '../../../types/agv';
import { useFleetStore } from '../../../store/fleetStore';

export function useFleetApi() {
  const { setFleet, removeAgv } = useFleetStore();

  const fetchFleet = useCallback(async () => {
    const res = await apiClient.get<ISimAgv[]>(ENDPOINTS.agvs.list);
    setFleet(res.data);
  }, [setFleet]);

  const createAgv = useCallback(async (req: ICreateAgvRequest) => {
    await apiClient.post(ENDPOINTS.agvs.create, req);
    message.success(`AGV ${req.serialNumber} created`);
    await fetchFleet();
  }, [fetchFleet]);

  const updateAgv = useCallback(async (id: string, req: ICreateAgvRequest) => {
    await apiClient.put(ENDPOINTS.agvs.update(id), req);
    message.success(`AGV ${req.serialNumber} updated`);
    await fetchFleet();
  }, [fetchFleet]);

  const deleteAgv = useCallback(async (id: string) => {
    await apiClient.delete(ENDPOINTS.agvs.delete(id));
    removeAgv(id);
    message.success(`AGV ${id} deleted`);
  }, [removeAgv]);

  const startAgv = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.agvs.start(id));
    message.success(`AGV ${id} started`);
  }, []);

  const stopAgv = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.agvs.stop(id));
    message.success(`AGV ${id} stopped`);
  }, []);

  const setPosition = useCallback(async (id: string, x: number, y: number, theta: number, mapId: string) => {
    await apiClient.post(ENDPOINTS.control.position(id), { x, y, theta, mapId });
  }, []);

  const setBattery = useCallback(async (id: string, batteryLevel: number, _charging: boolean) => {
    // Backend SetBatteryRequest binds to `Level` (not `batteryLevel`).
    // The `charging` flag has no corresponding backend DTO field and is intentionally omitted.
    await apiClient.post(ENDPOINTS.control.battery(id), { level: batteryLevel });
  }, []);

  const setSpeed = useCallback(async (id: string, speed: number) => {
    await apiClient.post(ENDPOINTS.control.speed(id), { speed });
  }, []);

  const addError = useCallback(async (id: string, errorType: string, errorDescription: string, errorLevel: string) => {
    await apiClient.post(ENDPOINTS.control.addError(id), { errorType, description: errorDescription, errorLevel });
    message.warning(`Error added to ${id}`);
  }, []);

  const clearErrors = useCallback(async (id: string) => {
    await apiClient.delete(ENDPOINTS.control.clearErrors(id));
    message.success(`Errors cleared for ${id}`);
  }, []);

  const injectTemplate = useCallback(async (id: string, templateName: string) => {
    await apiClient.post(ENDPOINTS.control.injectTemplate(id), { templateName });
    message.warning(`Template "${templateName}" injected on ${id}`);
  }, []);

  const liftAgv = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.control.lift(id));
    message.success(`AGV ${id} lifted`);
  }, []);

  const lowerAgv = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.control.lower(id));
    message.success(`AGV ${id} lowered`);
  }, []);

  const clearLoads = useCallback(async (id: string) => {
    const res = await apiClient.delete<{ cleared: number }>(ENDPOINTS.control.clearLoads(id));
    message.success(`AGV ${id} cleared ${res.data.cleared} load state(s)`);
    await fetchFleet();
  }, [fetchFleet]);

  const disconnect = useCallback(async (id: string, durationMs: number = 30000) => {
    // Backend TriggerDisconnect requires [FromBody] DisconnectRequest(int DurationMs).
    // Posting without a body causes 400 Bad Request under default ApiController binding rules.
    await apiClient.post(ENDPOINTS.control.disconnect(id), { durationMs });
    message.warning(`AGV ${id} disconnected`);
  }, []);

  const setOperatingMode = useCallback(async (id: string, mode: string) => {
    try {
      await apiClient.post(ENDPOINTS.control.operatingMode(id), { mode });
      message.success(`AGV ${id} → ${mode}`);
      await fetchFleet();
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      message.warning(msg ?? `AGV ${id} operating mode change rejected`);
    }
  }, [fetchFleet]);

  const clearOrderState = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.control.clearOrder(id));
    message.success(`AGV ${id} cleared order state`);
    await fetchFleet();
  }, [fetchFleet]);

  const setActionHold = useCallback(async (id: string, enabled: boolean) => {
    await apiClient.post(ENDPOINTS.control.actionHold(id), { enabled });
    message.success(`AGV ${id} action hold ${enabled ? 'ON' : 'OFF'}`);
    await fetchFleet();
  }, [fetchFleet]);

  const fetchInboundMessages = useCallback(async (id: string) => {
    const res = await apiClient.get<IInboundMqttMessage[]>(ENDPOINTS.control.inboundMessages(id));
    return res.data;
  }, []);

  // Gói state VDA5050 AGV đang publish — raw JSON cho tab Outbound
  const fetchOutboundState = useCallback(async (id: string): Promise<Record<string, unknown> | null> => {
    try {
      const res = await apiClient.get(ENDPOINTS.control.statePayload(id));
      return res.data;
    } catch {
      return null;
    }
  }, []);

  const fetchMapNodes = useCallback(async (): Promise<{ mapId: string; mapName: string; nodes: ISimMapNode[] }[]> => {
    const maps = (await apiClient.get<ISimMapInfo[]>(ENDPOINTS.maps.list)).data ?? [];
    const results = await Promise.all(maps.map(async (m) => ({
      mapId: m.mapId,
      mapName: m.mapName || m.mapId,
      nodes: (await apiClient.get<ISimMapNode[]>(ENDPOINTS.maps.nodes(m.mapId))).data ?? [],
    })));
    return results;
  }, []);

  // ── Bulk ops: ids = undefined → toàn bộ fleet ──────────────────────

  const bulkCreate = useCallback(async (req: IBulkCreateRequest): Promise<IBulkCreateResult> => {
    const res = await apiClient.post<IBulkCreateResult>(ENDPOINTS.fleet.bulkCreate, req);
    await fetchFleet();
    return res.data;
  }, [fetchFleet]);

  const startMany = useCallback(async (ids?: string[]) => {
    const res = await apiClient.post<{ started: number; total: number }>(
      ENDPOINTS.fleet.bulkStart, ids?.length ? { ids } : undefined);
    message.success(`Started ${res.data.started}/${res.data.total} AGVs`);
  }, []);

  const stopMany = useCallback(async (ids?: string[]) => {
    const res = await apiClient.post<{ stopped: number }>(
      ENDPOINTS.fleet.bulkStop, ids?.length ? { ids } : undefined);
    message.success(`Stopped ${res.data.stopped} AGVs`);
  }, []);

  const deleteMany = useCallback(async (ids?: string[]) => {
    const res = await apiClient.post<{ deleted: number }>(
      ENDPOINTS.fleet.bulkDelete, ids?.length ? { ids } : undefined);
    message.success(`Deleted ${res.data.deleted} AGVs`);
    await fetchFleet();
  }, [fetchFleet]);

  /** Import tạo lần lượt từng AGV — trả về số thành công + lỗi theo serial. */
  const importAgvs = useCallback(async (reqs: ICreateAgvRequest[]) => {
    const created: string[] = [];
    const failed: { serial: string; message: string }[] = [];
    for (const req of reqs) {
      try {
        await apiClient.post(ENDPOINTS.agvs.create, req);
        created.push(req.serialNumber);
      } catch (err: unknown) {
        const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
        failed.push({ serial: req.serialNumber, message: msg ?? 'request failed' });
      }
    }
    await fetchFleet();
    return { created, failed };
  }, [fetchFleet]);

  return {
    fetchFleet, createAgv, updateAgv, deleteAgv, startAgv, stopAgv,
    setPosition, setBattery, setSpeed, setOperatingMode, clearOrderState, setActionHold, fetchInboundMessages, fetchOutboundState, fetchMapNodes,
    addError, clearErrors, injectTemplate, liftAgv, lowerAgv, clearLoads, disconnect,
    bulkCreate, startMany, stopMany, deleteMany, importAgvs,
  };

}
