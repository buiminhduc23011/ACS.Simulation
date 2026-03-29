import { create } from 'zustand';
import type { IMapSummary, IMapDetail } from '../types/map';

interface MapState {
  maps: IMapSummary[];
  activeMapId: string | null;
  activeMapDetail: IMapDetail | null;
  setMaps: (maps: IMapSummary[]) => void;
  setActiveMap: (map: IMapDetail) => void;
  clearActiveMap: () => void;
}

export const useMapStore = create<MapState>((set) => ({
  maps: [],
  activeMapId: null,
  activeMapDetail: null,

  setMaps: (maps) => set({ maps }),

  setActiveMap: (map) => set({ activeMapDetail: map, activeMapId: map.mapId }),

  clearActiveMap: () => set({ activeMapDetail: null, activeMapId: null }),
}));
