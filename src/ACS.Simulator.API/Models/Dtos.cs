namespace ACS.Simulator.API.Models;

public record AgvMapMappingDto(
    string SourceMapId,
    string TargetMapId
);

// Request DTOs

public record CreateAgvRequest(
    string SerialNumber,
    string Manufacturer,
    string? IpAddress,
    string? MacAddress,
    double PosX,
    double PosY,
    double PosTheta,
    string MapId,
    int VehicleShapeId = 1,
    int NavigationTechnologyId = 1,
    List<AgvMapMappingDto>? MapMappings = null
);

public record UpdateAgvRequest(
    string SerialNumber,
    string Manufacturer,
    string? IpAddress,
    string? MacAddress,
    double PosX,
    double PosY,
    double PosTheta,
    string MapId,
    int VehicleShapeId = 1,
    int NavigationTechnologyId = 1,
    List<AgvMapMappingDto>? MapMappings = null
);

public record SetPositionRequest(double X, double Y, double Theta, string MapId);
public record SetBatteryRequest(double Level);
public record SetSpeedRequest(double Speed);
public record SetOperatingModeRequest(string Mode);

public record AddErrorRequest(
    string ErrorType,
    string ErrorLevel,
    string? Description,
    string? ErrorDescription
);

public record InjectTemplateRequest(string TemplateName);

public record ChaosSettingsRequest(
    bool LatencyEnabled,
    int LatencyMinMs,
    int LatencyMaxMs,
    bool PacketLossEnabled,
    int PacketLossPercent
);

public record DisconnectRequest(int DurationMs);

public record MqttConfigRequest(string Host, int Port);
public record AcsApiConfigRequest(string BaseUrl);
public record MqttConfigDto(string Host, int Port);
public record AcsApiConfigDto(string BaseUrl);

public record RunScenarioRequest(string? AgvId);

// Response DTOs

public record AgvSummaryDto(
    string Id,
    string SerialNumber,
    string Manufacturer,
    string IpAddress,
    string MacAddress,
    string Status,
    bool IsConnected,
    bool IsRunning,
    double PosX,
    double PosY,
    double PosTheta,
    string MapId,
    double BatteryLevel,
    bool IsCharging,
    bool HasErrors,
    int ErrorCount,
    double SpeedX,
    double SpeedY,
    int LoadCount,
    string OperatingMode = "AUTOMATIC",
    string OrderId = "",
    int OrderUpdateId = 0,
    List<AgvMapMappingDto>? MapMappings = null
);

public record InboundMqttMessageDto(
    DateTime Timestamp,
    string TopicType,
    string Topic,
    string Payload,
    bool Accepted,
    string? Note
);

public record AgvStateDto(
    string SerialNumber,
    bool Driving,
    bool Paused,
    double PosX,
    double PosY,
    double Theta,
    string MapId,
    double BatteryLevel,
    bool IsCharging,
    string OperatingMode,
    List<ErrorDto> Errors,
    bool SafetyStop
);

public record ErrorDto(string ErrorType, string ErrorLevel, string? Description);

public record FleetEventDto(
    string AgvId,
    string EventType,
    string Message,
    DateTime Timestamp
);

public record ScenarioSummaryDto(
    string Id,
    string Name,
    string Description,
    string Version,
    int StepCount,
    string FilePath
);

public record ScenarioRunDto(
    string RunId,
    string ScenarioName,
    string Status,
    int TotalSteps,
    int PassedSteps,
    int FailedSteps,
    DateTime StartedAt,
    DateTime? FinishedAt,
    List<StepResultDto> StepResults
);

public record StepResultDto(
    int Index,
    string StepType,
    string? AgvId,
    string? Comment,
    bool Passed,
    string? Message,
    long DurationMs
);

public record LogMessageDto(
    string AgvId,
    string Level,
    string Message,
    DateTime Timestamp
);

public record SimulatorConfigDto(
    MqttConfigDto Mqtt,
    AcsApiConfigDto AcsApi
);

public record MapSummaryProxyDto(
    int Id,
    string MapId,
    string MapName,
    string? MapDescription,
    int NodeCount,
    int EdgeCount,
    int StationCount
);
