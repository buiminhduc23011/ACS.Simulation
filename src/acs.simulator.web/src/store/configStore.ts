import { create } from 'zustand';

export interface MqttConfig {
  host: string;
  port: number;
}

export interface AcsApiConfig {
  baseUrl: string;
}

export interface SimulatorConfigState {
  mqtt: MqttConfig;
  acsApi: AcsApiConfig;
  loaded: boolean;
  setMqtt: (cfg: MqttConfig) => void;
  setAcsApi: (cfg: AcsApiConfig) => void;
  setConfig: (cfg: { mqtt: MqttConfig; acsApi: AcsApiConfig }) => void;
}

export const useConfigStore = create<SimulatorConfigState>((set) => ({
  mqtt: { host: '127.0.0.1', port: 1883 },
  acsApi: { baseUrl: 'http://localhost:9050' },
  loaded: false,

  setMqtt: (mqtt) => set({ mqtt }),
  setAcsApi: (acsApi) => set({ acsApi }),
  setConfig: ({ mqtt, acsApi }) => set({ mqtt, acsApi, loaded: true }),
}));
