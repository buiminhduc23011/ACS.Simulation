# Build và cài ACS Simulator

## Build

```powershell
cd C:\Users\ducbu\Documents\GitHub\ACS.Simulation\src\deploy
.\build-simulator.ps1
```

Kết quả:

- `src\dist-simulator\` — package chạy trực tiếp
- `src\dist-installer\ACS-Simulator-<version>-Setup.exe` — bộ cài Windows

Build mặc định self-contained, máy đích không cần cài .NET. Version lấy từ `src\AppVersion.props` (`AcsSimulatorVersion`).

Tùy chọn:

```powershell
# Chỉ build package, không compile Setup.exe
.\build-simulator.ps1 -SkipInstaller

# Build cho Linux
.\build-simulator.ps1 -Runtime linux-x64 -SkipInstaller
```

## Chuẩn bị trước khi cài

- Windows 10/11 64-bit, quyền Administrator
- MQTT broker đang chạy (Mosquitto hoặc tương đương)
- ACS API đang chạy nếu muốn simulator gọi sang ACS
- Biết các giá trị:
  - Simulator Port (mặc định `9060`)
  - MQTT Broker Address/Port (mặc định `127.0.0.1:1883`)
  - ACS API Base URL (mặc định `http://localhost:9050`)

## Cài đặt

1. Chạy `ACS-Simulator-<version>-Setup.exe` bằng quyền Administrator.
2. Nhập Simulator Port, MQTT Broker Address/Port và ACS API Base URL.
3. Bấm `Install`.

Bộ cài tự ghi `appsettings.Production.json`, cài/start service `ACS.Simulator` và tạo **một icon `ACS Simulator` trên desktop** để mở Web UI.

Không có tray app. Start Menu có thêm `ACS Simulator - Configure` để đổi cấu hình sau này.

## Log

- `C:\ACS.Simulator\logs\install-service.log`
- `C:\ACS.Simulator\server\logs\`
