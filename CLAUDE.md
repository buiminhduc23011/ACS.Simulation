# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ACS.Simulation is an AGV (Automated Guided Vehicle) fleet simulator for testing and chaos engineering. It implements the VDA5050 protocol and provides real-time fleet monitoring, error injection, and JSON-based scenario execution.

## Development Commands

### Backend (ASP.NET Core 8.0, .NET 8.0 SDK required)
```bash
cd ACS.Simulator.API
dotnet watch run          # Dev server with hot reload on http://localhost:9060
dotnet build              # Build only
dotnet run                # Run without watch
```

### Frontend (Node.js required)
```bash
cd acs.simulator.web
npm install               # Install dependencies (first time)
npm run dev               # Dev server on http://localhost:3001
npm run build             # TypeScript check + Vite production build
```

### Production Build
```bash
deploy/build-simulator.sh                         # Linux (framework-dependent)
deploy/build-simulator.sh --self-contained        # Linux (self-contained)
deploy/build-simulator.ps1                        # Windows
```
The build script compiles the frontend, publishes the backend, and embeds the frontend into `server/wwwroot` for single-binary deployment. Output goes to `dist-simulator/`.

## Architecture

### Two-process dev setup
- **Backend** (`ACS.Simulator.API/`, port 9060): ASP.NET Core REST API + SignalR hub + Swagger UI at `/swagger`
- **Frontend** (`acs.simulator.web/`, port 3001): React SPA. Vite proxies `/api/simulator` and `/hubs/simulator` to port 9060
- In production, the frontend is served as static files from the backend's `wwwroot/`

### Backend architecture
All services are registered as singletons in `Program.cs`. State is held **in-memory** with optional disk persistence via `FleetPersistenceService`.

Key service flow:
- **`SimulatorService`** — Fleet CRUD, manages a `ConcurrentDictionary<string, VirtualAgv>` of AGV instances
- **`VirtualAgv`** (in `Core/Services/`) — Core simulation engine (~2000 lines). Handles battery simulation, position tracking, MQTT publishing, operating modes, and error injection. Implements `IVirtualAgv`
- **`ScenarioRunnerService`** — Executes JSON scenario files step-by-step (10 step types: create_agv, start_agv, wait, inject_error, etc.)
- **`SimulatorBroadcastService`** — Background `IHostedService` that pushes fleet state via SignalR on a timer
- **`SimulatorHub`** (in `Hubs/`) — SignalR hub at `/hubs/simulator` for real-time WebSocket communication

Controllers map to REST routes under `/api/simulator/`:
- `AgvController` — Fleet CRUD and state
- `AgvControlController` — Position, battery, speed, lift, error injection, chaos controls
- `MapController` — Map data from ACS API proxy
- `ScenarioController` — Scenario import/run/stop
- `ConfigController` — Runtime MQTT and ACS API config

### Frontend architecture
- **State management**: Zustand stores in `src/store/` (fleetStore, mapStore, scenarioStore, configStore)
- **API layer**: Axios client in `src/infrastructure/api/apiClient.ts`, all endpoints defined in `endpoints.ts` under `/api/simulator`
- **Real-time**: SignalR React hook in `src/infrastructure/signalr/useSimulatorSignalR.ts`
- **Features**: Each page module in `src/features/` (dashboard, fleet, map, chaos, scenario, settings) is self-contained with its own `pages/` and optional `components/`/`hooks/`
- **UI library**: Ant Design 5.x, map canvas uses react-konva, charts use Recharts
- **Path alias**: `@` maps to `./src` (configured in `vite.config.ts`)

## Configuration

- **Backend**: `ACS.Simulator.API/appsettings.json` — Listening port (9060), MQTT broker (127.0.0.1:1883), ACS API base URL (localhost:9050), Serilog levels
- **Frontend**: `acs.simulator.web/src/config/config.ts` — API base URL, SignalR URL, pixel scale factor
- **Runtime config**: Stored in `data/runtime-config.json` relative to app base directory, loaded on startup

## Scenarios

Pre-built test scenarios live in `ACS.Simulator.API/Scenarios/` (9 JSON files). They cover basic movement, battery lifecycle, emergency stop, fleet startup, stress testing, deadlock detection, error injection, network chaos, and cascading failures. Scenarios are imported and executed through the web UI or the `/api/simulator/scenarios` endpoints.

## Notes

- Comments in config files and some code are in Vietnamese
- Logs write to `logs/simulator-{date}.log` with 7-day retention (Serilog)
- No authentication — intended for internal/test use
- MQTT integration via MQTTnet for VDA5050 protocol communication
