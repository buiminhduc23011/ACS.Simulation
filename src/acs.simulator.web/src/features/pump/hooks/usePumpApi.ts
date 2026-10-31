import { useCallback } from 'react';
import { message } from 'antd';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import type { IPumpConfigRequest, IPumpSnapshot, IWorkflowSummary } from '../../../types/pump';

export function usePumpApi() {
  const fetchState = useCallback(async (): Promise<IPumpSnapshot | null> => {
    try {
      const res = await apiClient.get<IPumpSnapshot>(ENDPOINTS.pump.state);
      return res.data;
    } catch {
      return null;
    }
  }, []);

  const saveConfig = useCallback(async (req: IPumpConfigRequest): Promise<IPumpSnapshot> => {
    const res = await apiClient.put<IPumpSnapshot>(ENDPOINTS.pump.config, req);
    return res.data;
  }, []);

  const setEnabled = useCallback(async (enabled: boolean): Promise<IPumpSnapshot> => {
    const res = await apiClient.post<IPumpSnapshot>(ENDPOINTS.pump.enabled, { enabled });
    return res.data;
  }, []);

  const loadWorkflows = useCallback(async (): Promise<IWorkflowSummary[]> => {
    const res = await apiClient.get<IWorkflowSummary[]>(ENDPOINTS.pump.workflows);
    return res.data;
  }, []);

  const cancelTransaction = useCallback(async (txId: number) => {
    await apiClient.post(ENDPOINTS.pump.cancelTx(txId));
    message.success(`Cancel requested for tx ${txId}`);
  }, []);

  return { fetchState, saveConfig, setEnabled, loadWorkflows, cancelTransaction };
}
