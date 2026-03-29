import { create } from 'zustand';
import type { ISimAgv } from '../types/agv';
import type { IFleetEvent } from '../types/events';

const MAX_EVENTS = 200;

interface FleetState {
  agvs: Record<string, ISimAgv>;
  events: IFleetEvent[];
  setFleet: (agvs: ISimAgv[]) => void;
  updateAgv: (agv: ISimAgv) => void;
  removeAgv: (id: string) => void;
  addEvent: (event: IFleetEvent) => void;
  clearEvents: () => void;
}

export const useFleetStore = create<FleetState>((set) => ({
  agvs: {},
  events: [],

  setFleet: (agvs) =>
    set({ agvs: Object.fromEntries(agvs.map((a) => [a.id, a])) }),

  updateAgv: (agv) =>
    set((s) => ({ agvs: { ...s.agvs, [agv.id]: agv } })),

  removeAgv: (id) =>
    set((s) => {
      const copy = { ...s.agvs };
      delete copy[id];
      return { agvs: copy };
    }),

  addEvent: (event) =>
    set((s) => ({
      events: [event, ...s.events].slice(0, MAX_EVENTS),
    })),

  clearEvents: () => set({ events: [] }),
}));
