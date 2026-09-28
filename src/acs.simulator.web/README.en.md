# ACS Simulator Web

A web-based AGV (Automated Guided Vehicle) simulator interface built with React 18 + TypeScript + Vite. The application connects to the ASP.NET backend (`ACS.Simulator.API`) via REST API and SignalR to monitor and control an AGV fleet in real time.

---

## Prerequisites

| Tool | Minimum version |
|------|-----------------|
| Node.js | 18.x |
| npm | 9.x |
| .NET | 8.0 (for the backend) |

---

## Getting Started

### 1. Install dependencies

```bash
cd src/acs.simulator.web
npm install
```

### 2. Start the development server

```bash
npm run dev
```

The app will be available at: **http://localhost:3001**

> The `ACS.Simulator.API` backend must be running on port **9060** for full functionality.

### 3. Production build

```bash
npm run build
```

Output is placed in the `dist/` directory.

### 4. Preview the production build locally

```bash
npm run preview
```

---

## Configuration

Main config file: `src/config/config.ts`

```ts
export const API_BASE_URL     = 'http://localhost:9060';          // Backend API base URL
export const SIGNALR_URL      = `${API_BASE_URL}/hubs/simulator`; // SignalR Hub URL
export const METERS_TO_PIXELS = 60;                               // Map render scale
```

The Vite dev server automatically proxies (via `vite.config.ts`):
- `/api/simulator/*` → `http://localhost:9060`
- `/hubs/simulator` → `http://localhost:9060` (WebSocket)

---

## Features

### ACS compatibility mode for rolling order updates

`ACS.Simulator.API` intentionally follows the latest received VDA5050 order payload as-is when ACS publishes rolling route updates.

This is a simulator compatibility mode for real deployed AGVs. It does **not** try to reconstruct a strict VDA5050 reference-receiver route by merging older released prefixes or synthesizing missing edges from previous payloads.

Use this mode when you want the simulator to behave like a physical AGV that primarily "receives the newest order and keeps moving". If you are validating strict VDA5050 update stitching semantics, treat the simulator as a compatibility receiver, not as the protocol reference implementation.

---

### Dashboard
Fleet-wide overview:
- **6 stat cards**: Total AGVs, running, error, offline, charging, low battery
- **Status pie chart** of fleet (Recharts)
- **Horizontal battery bar chart** per AGV
- **Live event log** fed from SignalR

### Fleet Management
- Sortable / filterable AGV table (filter by status, sort by battery)
- Create AGV (serial number, initial position, manufacturer, etc.)
- Delete AGV from fleet
- **Per-AGV control drawer** with tabs:
  - **Position**: Set X, Y, Theta coordinates
  - **Battery**: Set charge level, toggle charging state
  - **Speed**: Adjust max speed
  - **Errors**: Manually inject errors or clear all active errors
  - **Network**: Trigger connection drop

### Settings
- Configure MQTT broker (host, port, username/password)
- Configure the main ACS API base URL

---

## Project Structure

```
src/
├── config/
│   └── config.ts                  # Global constants
├── types/
│   ├── agv.ts                     # ISimAgv, AgvStatus, ICreateAgvRequest
│   └── events.ts                  # IFleetEvent, ILogMessage
├── infrastructure/
│   ├── api/
│   │   ├── apiClient.ts           # Axios instance
│   │   └── endpoints.ts           # Typed API route constants
│   └── signalr/
│       └── useSimulatorSignalR.ts # React hook for the SignalR hub
├── store/
│   ├── fleetStore.ts              # Zustand: AGV map + event log
│   └── configStore.ts             # Zustand: MQTT / API config
├── features/
│   ├── fleet/                     # FleetPage, CreateAgvModal, AgvControlPage
│   ├── dashboard/                 # DashboardPage (charts + stats)
│   └── settings/                  # SettingsPage (MQTT + API config)
├── layouts/
│   └── AppLayout.tsx              # Dark sider + header + SignalR root
└── App.tsx                        # Ant Design dark ConfigProvider + Router + Routes
```

---

## Real-time Communication (SignalR)

Hub URL: `http://localhost:9060/hubs/simulator`

| Group | Event | Description |
|-------|-------|-------------|
| `fleet` | `AgvFleetUpdated` | Full fleet snapshot, pushed every 300 ms |
| `fleet` | `FleetEvent` | Discrete events (created, deleted, status change, …) |

---

## Tech Stack

| Library | Version | Purpose |
|---------|---------|---------|
| React | 18.3 | UI framework |
| TypeScript | 5.7 | Static typing |
| Vite | 6.x | Build tool & dev server |
| Ant Design | 5.26 | UI component library |
| @ant-design/icons | 5.6 | Icon set |
| Zustand | 5.0 | Lightweight state management |
| Axios | 1.10 | HTTP client |
| @microsoft/signalr | 8.0 | SignalR client |
| Recharts | 2.15 | Charts (pie, bar) |
| react-router-dom | 7.6 | Client-side routing |

---

## Related Projects

- Backend API: [`src/ACS.Simulator.API`](../../backend/ACS.Simulator.API/)
- Console Simulator: [`Simulator/ACS.AgvSimulator.Console`](../../../Simulator/ACS.AgvSimulator.Console/)
- Main project docs: [`README.md`](../../../README.md)
