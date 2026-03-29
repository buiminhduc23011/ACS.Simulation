import { useCallback } from 'react';
import { message } from 'antd';
import apiClient from '../../../infrastructure/api/apiClient';
import { ENDPOINTS } from '../../../infrastructure/api/endpoints';
import type { ISimAgv } from '../../../types/agv';
import type { ICreateAgvRequest } from '../../../types/agv';
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

  const setBattery = useCallback(async (id: string, batteryLevel: number, charging: boolean) => {
    await apiClient.post(ENDPOINTS.control.battery(id), { batteryLevel, charging });
  }, []);

  const setSpeed = useCallback(async (id: string, speed: number) => {
    await apiClient.post(ENDPOINTS.control.speed(id), { speed });
  }, []);

  const addError = useCallback(async (id: string, errorType: string, errorDescription: string, errorLevel: string) => {
    await apiClient.post(ENDPOINTS.control.addError(id), { errorType, errorDescription, errorLevel });
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

  const disconnect = useCallback(async (id: string) => {
    await apiClient.post(ENDPOINTS.control.disconnect(id));
    message.warning(`AGV ${id} disconnected`);
  }, []);

  return { fetchFleet, createAgv, updateAgv, deleteAgv, startAgv, stopAgv, setPosition, setBattery, setSpeed, addError, clearErrors, injectTemplate, liftAgv, lowerAgv, disconnect };
}
