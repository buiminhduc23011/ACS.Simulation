const BASE = '/api/simulator';

export const ENDPOINTS = {
  // AGV Fleet
  agvs: {
    list: `${BASE}/agvs`,
    create: `${BASE}/agvs`,
    update: (id: string) => `${BASE}/agvs/${id}`,
    delete: (id: string) => `${BASE}/agvs/${id}`,
    start: (id: string) => `${BASE}/agvs/${id}/start`,
    stop: (id: string) => `${BASE}/agvs/${id}/stop`,
  },
  // AGV Control
  control: {
    position: (id: string) => `${BASE}/agvs/${id}/control/position`,
    battery: (id: string) => `${BASE}/agvs/${id}/control/battery`,
    speed: (id: string) => `${BASE}/agvs/${id}/control/speed`,
    operatingMode: (id: string) => `${BASE}/agvs/${id}/control/operating-mode`,
    clearOrder: (id: string) => `${BASE}/agvs/${id}/control/clear-order`,
    inboundMessages: (id: string) => `${BASE}/agvs/${id}/control/inbound-messages`,
    lift: (id: string) => `${BASE}/agvs/${id}/control/lift`,
    lower: (id: string) => `${BASE}/agvs/${id}/control/lower`,
    clearLoads: (id: string) => `${BASE}/agvs/${id}/control/loads`,
    addError: (id: string) => `${BASE}/agvs/${id}/control/errors`,
    clearErrors: (id: string) => `${BASE}/agvs/${id}/control/errors`,
    injectTemplate: (id: string) => `${BASE}/agvs/${id}/control/errors/inject`,
    disconnect: (id: string) => `${BASE}/agvs/${id}/control/disconnect`,
  },
  // Error Templates
  errorTemplates: `${BASE}/error-templates`,
  // Config
  config: {
    get: `${BASE}/config`,
    updateMqtt: `${BASE}/config/mqtt`,
    updateAcsApi: `${BASE}/config/acs-api`,
  },
} as const;
