# ACS Simulator Web

Giao diện web cho hệ thống mô phỏng AGV (Automated Guided Vehicle), được xây dựng bằng React 18 + TypeScript + Vite. Ứng dụng kết nối với backend ASP.NET (`ACS.Simulator.API`) qua REST API và SignalR để theo dõi và điều khiển đội xe AGV theo thời gian thực.

---

## Yêu cầu hệ thống

| Công cụ | Phiên bản tối thiểu |
|---------|---------------------|
| Node.js | 18.x trở lên |
| npm | 9.x trở lên |
| .NET | 8.0 (để chạy backend) |

---

## Cài đặt và chạy

### 1. Cài dependencies

```bash
cd src/frontend/acs.simulator.web
npm install
```

### 2. Chạy môi trường phát triển

```bash
npm run dev
```

Ứng dụng sẽ chạy tại: **http://localhost:3001**

> Backend API (`ACS.Simulator.API`) cần chạy tại port **9060** trước khi sử dụng đầy đủ tính năng.

### 3. Build production

```bash
npm run build
```

Output sẽ nằm trong thư mục `dist/`.

### 4. Xem trước bản build

```bash
npm run preview
```

---

## Cấu hình

File cấu hình chính: `src/config/config.ts`

```ts
export const API_BASE_URL = 'http://localhost:9060';      // Địa chỉ backend API
export const SIGNALR_URL  = `${API_BASE_URL}/hubs/simulator`; // Địa chỉ SignalR Hub
export const METERS_TO_PIXELS = 60;                        // Tỷ lệ hiển thị bản đồ
```

Proxy Vite (trong `vite.config.ts`) tự động chuyển tiếp:
- `/api/simulator/*` → `http://localhost:9060`
- `/hubs/simulator` → `http://localhost:9060` (WebSocket)

---

## Tính năng

### Dashboard
Màn hình tổng quan toàn đội xe:
- **6 thẻ thống kê**: Tổng AGV, đang chạy, lỗi, offline, đang sạc pin, pin thấp
- **Biểu đồ tròn** trạng thái AGV (Recharts)
- **Biểu đồ thanh ngang** mức pin từng xe
- **Nhật ký sự kiện** thời gian thực (SignalR)

### Fleet Management — Quản lý đội xe
- Bảng danh sách AGV với lọc theo trạng thái và sắp xếp theo pin
- Tạo mới AGV (nhập Serial Number, vị trí ban đầu, nhà sản xuất...)
- Xóa AGV khỏi đội
- **Drawer điều khiển chi tiết** từng xe:
  - **Position**: Đặt tọa độ X, Y, Theta
  - **Battery**: Đặt mức pin, bật/tắt sạc
  - **Speed**: Điều chỉnh tốc độ tối đa
  - **Errors**: Inject lỗi thủ công hoặc xóa tất cả lỗi
  - **Network**: Kích hoạt ngắt kết nối

### Map Monitor — Theo dõi bản đồ
- Canvas Konva hiển thị đồ thị bản đồ (cạnh, nút, trạm dừng)
- AGV được vẽ theo vị trí và góc theta thực tế
- Zoom & pan bằng chuột (react-zoom-pan-pinch)
- Chọn bản đồ từ ACS API và kích hoạt cho toàn bộ đội xe

### Chaos Engineering — Kiểm thử độ tin cậy
- **8 mẫu lỗi** có sẵn (click để inject ngay lập tức)
- **Sliders** điều chỉnh độ trễ mạng (latency) và tỷ lệ mất gói (packet loss)
- Xem bảng tất cả lỗi đang hoạt động trên toàn đội
- Xóa lỗi hoặc ngắt kết nối từng xe

### Scenario Runner — Chạy kịch bản
- Import file JSON kịch bản từ máy tính
- Chạy / dừng kịch bản theo yêu cầu
- Thanh tiến trình và danh sách kết quả từng bước (pass/fail)
- **Terminal log** tối màu hiển thị log theo thời gian thực từ SignalR

### Settings — Cài đặt
- Cấu hình MQTT Broker (host, port, username/password)
- Cấu hình URL ACS API chính

---

## Kiến trúc

```
src/
├── config/
│   └── config.ts                  # Hằng số cấu hình toàn cục
├── types/
│   ├── agv.ts                     # ISimAgv, AgvStatus, ICreateAgvRequest
│   ├── map.ts                     # IMapSummary, IMapDetail, IMapNode/Edge/Station
│   ├── scenario.ts                # IScenario, IScenarioRun, IStepResult
│   ├── chaos.ts                   # IErrorTemplate, IChaosSettings
│   └── events.ts                  # IFleetEvent, ILogMessage
├── infrastructure/
│   ├── api/
│   │   ├── apiClient.ts           # Axios instance với base URL
│   │   └── endpoints.ts           # Tất cả route API dưới dạng hằng số
│   └── signalr/
│       └── useSimulatorSignalR.ts # React hook kết nối SignalR Hub
├── store/
│   ├── fleetStore.ts              # Zustand: danh sách AGV + sự kiện
│   ├── mapStore.ts                # Zustand: bản đồ đang chọn
│   ├── scenarioStore.ts           # Zustand: kịch bản + log
│   └── configStore.ts             # Zustand: cấu hình MQTT/API
├── features/
│   ├── fleet/                     # FleetPage, CreateAgvModal, AgvDetailDrawer
│   ├── dashboard/                 # DashboardPage (charts + stats)
│   ├── map/                       # MapMonitorPage, MapCanvas (Konva)
│   ├── chaos/                     # ChaosPage (templates + network sliders)
│   ├── scenario/                  # ScenarioPage (runner + log console)
│   └── settings/                  # SettingsPage (MQTT + API config)
├── layouts/
│   └── AppLayout.tsx              # Sider dark theme + Header + SignalR root
└── App.tsx                        # ConfigProvider (dark) + BrowserRouter + Routes
```

---

## Giao tiếp thời gian thực (SignalR)

Hub URL: `http://localhost:9060/hubs/simulator`

| Group | Event | Mô tả |
|-------|-------|-------|
| `fleet` | `AgvFleetUpdated` | Danh sách toàn bộ AGV, cập nhật 300ms/lần |
| `fleet` | `FleetEvent` | Sự kiện đơn lẻ (tạo, xóa, đổi trạng thái...) |
| `scenario` | `ScenarioStarted` | Kịch bản bắt đầu chạy |
| `scenario` | `ScenarioStep` | Kết quả từng bước |
| `scenario` | `ScenarioProgress` | Cập nhật tiến trình |
| `scenario` | `ScenarioFinished` | Kịch bản kết thúc |
| `scenario` | `LogMessage` | Log text từ server |

---

## Định dạng file Scenario (JSON)

```json
{
  "id": "scenario-001",
  "name": "Test Emergency Stop",
  "description": "Mô phỏng dừng khẩn cấp",
  "steps": [
    { "type": "SetPosition", "agvId": "SN001", "x": 5.0, "y": 3.0, "theta": 0 },
    { "type": "WaitStep", "durationMs": 2000 },
    { "type": "InjectError", "agvId": "SN001", "errorType": "EMERGENCY_STOP", "errorLevel": "FATAL", "errorDescription": "Test fatal error" },
    { "type": "WaitStep", "durationMs": 3000 },
    { "type": "ClearError", "agvId": "SN001" },
    { "type": "LogStep", "message": "Scenario completed" }
  ]
}
```

---

## Stack công nghệ

| Thư viện | Phiên bản | Mục đích |
|----------|-----------|---------|
| React | 18.3 | UI framework |
| TypeScript | 5.7 | Type safety |
| Vite | 6.x | Build tool |
| Ant Design | 5.26 | UI components |
| @ant-design/icons | 5.6 | Icon set |
| Zustand | 5.0 | State management |
| Axios | 1.10 | HTTP client |
| @microsoft/signalr | 8.0 | SignalR client |
| react-konva | 18.2 | Canvas 2D (bản đồ) |
| Recharts | 2.15 | Biểu đồ |
| react-zoom-pan-pinch | 3.7 | Zoom/pan bản đồ |
| react-router-dom | 7.6 | Routing |

---

## Liên kết

- Backend API: [`src/backend/ACS.Simulator.API`](../../backend/ACS.Simulator.API/)
- Simulator Console: [`Simulator/ACS.AgvSimulator.Console`](../../../Simulator/ACS.AgvSimulator.Console/)
- Tài liệu tổng thể: [`README.md`](../../../README.md)
