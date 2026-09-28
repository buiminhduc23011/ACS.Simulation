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
cd src/acs.simulator.web
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

### Chế độ tương thích ACS cho rolling order update

`ACS.Simulator.API` được chủ đích thiết kế để bám theo nguyên payload VDA5050 mới nhất mà ACS vừa gửi khi hệ thống publish các bản cập nhật route kiểu rolling.

Đây là chế độ tương thích để mô phỏng AGV thực tế đang triển khai. Simulator sẽ **không** cố dựng lại route theo kiểu reference receiver VDA5050 strict bằng cách ghép prefix released cũ hoặc tự sinh thêm cạnh còn thiếu từ payload trước đó.

Hãy dùng chế độ này khi mục tiêu là làm simulator hành xử giống AGV thật theo kiểu “nhận order mới nhất rồi chạy tiếp”. Nếu cần kiểm thử strict update stitching theo VDA5050, xem simulator này là compatibility receiver chứ không phải implementation tham chiếu của giao thức.

---

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
│   └── events.ts                  # IFleetEvent, ILogMessage
├── infrastructure/
│   ├── api/
│   │   ├── apiClient.ts           # Axios instance với base URL
│   │   └── endpoints.ts           # Tất cả route API dưới dạng hằng số
│   └── signalr/
│       └── useSimulatorSignalR.ts # React hook kết nối SignalR Hub
├── store/
│   ├── fleetStore.ts              # Zustand: danh sách AGV + sự kiện
│   └── configStore.ts             # Zustand: cấu hình MQTT/API
├── features/
│   ├── fleet/                     # FleetPage, CreateAgvModal, AgvControlPage
│   ├── dashboard/                 # DashboardPage (charts + stats)
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
| Recharts | 2.15 | Biểu đồ |
| react-router-dom | 7.6 | Routing |

---

## Liên kết

- Backend API: [`src/ACS.Simulator.API`](../../backend/ACS.Simulator.API/)
- Simulator Console: [`Simulator/ACS.AgvSimulator.Console`](../../../Simulator/ACS.AgvSimulator.Console/)
- Tài liệu tổng thể: [`README.md`](../../../README.md)
