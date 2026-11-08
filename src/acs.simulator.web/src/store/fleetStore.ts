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

  // Giữ ref cũ cho xe không đổi để selector useShallow không re-render thừa.
  // SignalR push full fleet mỗi tick — không có check này thì 34 AGV re-render toàn bảng.
  setFleet: (agvs) =>
    set((s) => {
      const prev = s.agvs;
      let changed = false;
      // Xóa xe không còn trong fleet
      for (const id of Object.keys(prev)) {
        if (!agvs.some((a) => a.id === id)) {
          changed = true;
          break;
        }
      }
      const next: Record<string, ISimAgv> = {};
      for (const a of agvs) {
        const old = prev[a.id];
        if (old && isAgvEqual(old, a)) {
          next[a.id] = old; // tái dùng ref cũ
        } else {
          next[a.id] = a;
          changed = true;
        }
      }
      if (!changed && Object.keys(next).length === Object.keys(prev).length) return s;
      return { agvs: next };
    }),

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

/** So sánh nông đủ cho UI list: bỏ qua object identity mới mỗi tick SignalR. */
function isAgvEqual(a: ISimAgv, b: ISimAgv): boolean {
  return (
    a.id === b.id &&
    a.serialNumber === b.serialNumber &&
    a.manufacturer === b.manufacturer &&
    a.ipAddress === b.ipAddress &&
    a.macAddress === b.macAddress &&
    a.status === b.status &&
    a.isConnected === b.isConnected &&
    a.isRunning === b.isRunning &&
    a.holdActions === b.holdActions &&
    a.orderEchoDelaySeconds === b.orderEchoDelaySeconds &&
    a.posX === b.posX &&
    a.posY === b.posY &&
    a.posTheta === b.posTheta &&
    a.mapId === b.mapId &&
    a.batteryLevel === b.batteryLevel &&
    a.isCharging === b.isCharging &&
    a.hasErrors === b.hasErrors &&
    a.errorCount === b.errorCount &&
    a.speedX === b.speedX &&
    a.speedY === b.speedY &&
    a.loadCount === b.loadCount &&
    a.operatingMode === b.operatingMode &&
    a.orderId === b.orderId &&
    a.orderUpdateId === b.orderUpdateId &&
    a.vehicleShapeId === b.vehicleShapeId &&
    a.navigationTechnologyId === b.navigationTechnologyId &&
    a.motion?.maxSpeed === b.motion?.maxSpeed &&
    a.motion?.acceleration === b.motion?.acceleration &&
    a.motion?.deceleration === b.motion?.deceleration &&
    a.actions?.liftDurationMs === b.actions?.liftDurationMs &&
    a.actions?.lowerDurationMs === b.actions?.lowerDurationMs &&
    a.actions?.genericActionDurationMs === b.actions?.genericActionDurationMs &&
    a.actions?.loadStateDelayMs === b.actions?.loadStateDelayMs &&
    a.battery?.fullChargeHours === b.battery?.fullChargeHours &&
    a.battery?.chargeRatePerMinute === b.battery?.chargeRatePerMinute &&
    a.battery?.continuousOperatingHours === b.battery?.continuousOperatingHours &&
    a.battery?.drainRatePerMinute === b.battery?.drainRatePerMinute &&
    a.activeNotification?.type === b.activeNotification?.type &&
    a.activeNotification?.message === b.activeNotification?.message &&
    mapMappingsEqual(a.mapMappings, b.mapMappings)
  );
}

function mapMappingsEqual(
  a: ISimAgv['mapMappings'],
  b: ISimAgv['mapMappings'],
): boolean {
  if (a === b) return true;
  if (!a || !b) return a === b;
  if (a.length !== b.length) return false;
  return a.every(
    (m, i) => m.sourceMapId === b[i]?.sourceMapId && m.targetMapId === b[i]?.targetMapId,
  );
}
