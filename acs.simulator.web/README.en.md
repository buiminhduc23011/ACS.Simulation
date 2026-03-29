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
cd src/frontend/acs.simulator.web
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

### Map Monitor
- Konva canvas rendering the map graph (edges, nodes, stations)
- AGVs drawn at their live position and theta heading
- Mouse zoom & pan (react-zoom-pan-pinch)
- Select any map from the ACS API and activate it fleet-wide

### Chaos Engineering
- **8 preset error templates** — click a card to inject instantly
- **Network chaos sliders**: latency range (ms) and packet loss (%)
- Active errors table showing all faults across the entire fleet
- Clear errors or trigger disconnect per AGV

### Scenario Runner
- Import JSON scenario files from disk
- Start / stop scenario execution on demand
- Progress bar with per-step pass/fail result timeline
- **Dark terminal log console** streaming `LogMessage` events via SignalR

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
│   ├── map.ts                     # IMapSummary, IMapDetail, IMapNode/Edge/Station
│   ├── scenario.ts                # IScenario, IScenarioRun, IStepResult
│   ├── chaos.ts                   # IErrorTemplate, IChaosSettings
│   └── events.ts                  # IFleetEvent, ILogMessage
├── infrastructure/
│   ├── api/
│   │   ├── apiClient.ts           # Axios instance
│   │   └── endpoints.ts           # Typed API route constants
│   └── signalr/
│       └── useSimulatorSignalR.ts # React hook for the SignalR hub
├── store/
│   ├── fleetStore.ts              # Zustand: AGV map + event log
│   ├── mapStore.ts                # Zustand: selected map detail
│   ├── scenarioStore.ts           # Zustand: scenarios + run state + logs
│   └── configStore.ts             # Zustand: MQTT / API config
├── features/
│   ├── fleet/                     # FleetPage, CreateAgvModal, AgvDetailDrawer
│   ├── dashboard/                 # DashboardPage (charts + stats)
│   ├── map/                       # MapMonitorPage, MapCanvas (Konva)
│   ├── chaos/                     # ChaosPage (templates + network sliders)
│   ├── scenario/                  # ScenarioPage (runner + log console)
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
| `scenario` | `ScenarioStarted` | Scenario execution begins |
| `scenario` | `ScenarioStep` | Result of a single step |
| `scenario` | `ScenarioProgress` | Rolling progress update |
| `scenario` | `ScenarioFinished` | Scenario completed / failed / stopped |
| `scenario` | `LogMessage` | Free-text log line from the server |

---

## Scenario File Format (JSON)

```json
{
  "id": "scenario-001",
  "name": "Test Emergency Stop",
  "description": "Simulate an AGV emergency-stop fault",
  "steps": [
    { "type": "SetPosition",  "agvId": "SN001", "x": 5.0, "y": 3.0, "theta": 0 },
    { "type": "WaitStep",     "durationMs": 2000 },
    { "type": "InjectError",  "agvId": "SN001", "errorType": "EMERGENCY_STOP",
      "errorLevel": "FATAL", "errorDescription": "Simulated fatal fault" },
    { "type": "WaitStep",     "durationMs": 3000 },
    { "type": "ClearError",   "agvId": "SN001" },
    { "type": "LogStep",      "message": "Scenario complete" }
  ]
}
```

Supported step types: `WaitStep`, `SetPosition`, `SetBattery`, `SetSpeed`, `InjectError`, `ClearError`, `DisconnectStep`, `SetChaos`, `AssertStep`, `LogStep`.

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
| react-konva | 18.2 | 2D canvas (map rendering) |
| Recharts | 2.15 | Charts (pie, bar) |
| react-zoom-pan-pinch | 3.7 | Zoom / pan on Konva canvas |
| react-router-dom | 7.6 | Client-side routing |

---

## Related Projects

- Backend API: [`src/backend/ACS.Simulator.API`](../../backend/ACS.Simulator.API/)
- Console Simulator: [`Simulator/ACS.AgvSimulator.Console`](../../../Simulator/ACS.AgvSimulator.Console/)
- Main project docs: [`README.md`](../../../README.md)
