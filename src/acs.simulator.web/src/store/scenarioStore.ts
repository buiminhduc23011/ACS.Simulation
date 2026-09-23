import { create } from 'zustand';
import type { IScenario, IScenarioRun, IStepResult } from '../types/scenario';
import type { ILogMessage } from '../types/events';

const MAX_LOGS = 500;

interface ScenarioState {
  scenarios: IScenario[];
  currentRun: IScenarioRun | null;
  logs: ILogMessage[];
  setScenarios: (s: IScenario[]) => void;
  setCurrentRun: (run: IScenarioRun | null) => void;
  appendStepResult: (result: IStepResult) => void;
  appendLog: (log: ILogMessage) => void;
  clearLogs: () => void;
}

export const useScenarioStore = create<ScenarioState>((set) => ({
  scenarios: [],
  currentRun: null,
  logs: [],

  setScenarios: (scenarios) => set({ scenarios }),

  setCurrentRun: (run) => set({ currentRun: run }),

  appendStepResult: (result) =>
    set((s) => {
      if (!s.currentRun) return s;
      const stepResults = [...(s.currentRun.stepResults ?? []), result];
      return { currentRun: { ...s.currentRun, stepResults } };
    }),

  appendLog: (log) =>
    set((s) => ({ logs: [...s.logs, log].slice(-MAX_LOGS) })),

  clearLogs: () => set({ logs: [] }),
}));
