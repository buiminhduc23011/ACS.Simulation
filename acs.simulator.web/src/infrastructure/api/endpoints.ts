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
    state: (id: string) => `${BASE}/agvs/${id}/state`,
  },
  // AGV Control
  control: {
    position: (id: string) => `${BASE}/agvs/${id}/control/position`,
    battery: (id: string) => `${BASE}/agvs/${id}/control/battery`,
    speed: (id: string) => `${BASE}/agvs/${id}/control/speed`,
    lift: (id: string) => `${BASE}/agvs/${id}/control/lift`,
    lower: (id: string) => `${BASE}/agvs/${id}/control/lower`,
    addError: (id: string) => `${BASE}/agvs/${id}/control/errors`,
    clearErrors: (id: string) => `${BASE}/agvs/${id}/control/errors`,
    injectTemplate: (id: string) => `${BASE}/agvs/${id}/control/errors/inject`,
    chaos: (id: string) => `${BASE}/agvs/${id}/control/chaos`,
    disconnect: (id: string) => `${BASE}/agvs/${id}/control/disconnect`,
  },
  // Error Templates
  errorTemplates: `${BASE}/error-templates`,
  // Maps
  maps: {
    list: `${BASE}/maps`,
    detail: (mapId: string) => `${BASE}/maps/${mapId}`,
    activate: (mapId: string) => `${BASE}/maps/${mapId}/activate`,
  },
  // Scenarios
  scenarios: {
    list: `${BASE}/scenarios`,
    import: `${BASE}/scenarios/import`,
    download: (id: string) => `${BASE}/scenarios/${id}/download`,
    run: (id: string) => `${BASE}/scenarios/${id}/run`,
    stop: `${BASE}/scenarios/stop`,
    currentRun: `${BASE}/scenarios/current-run`,
  },
  // Config
  config: {
    get: `${BASE}/config`,
    updateMqtt: `${BASE}/config/mqtt`,
    updateAcsApi: `${BASE}/config/acs-api`,
  },
} as const;
