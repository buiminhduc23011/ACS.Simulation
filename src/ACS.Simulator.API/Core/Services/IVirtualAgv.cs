using ACS.Simulator.API.Core.Models;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Interface for Virtual AGV - allows mocking in tests.
/// </summary>
public interface IVirtualAgv : IDisposable
{
    // Identity
    string SerialNumber { get; }
    string IpAddress { get; }
    string MacAddress { get; }

    // Status
    bool IsConnected { get; }
    bool IsRunning { get; }
    bool IsHoldingActions { get; }
    Vda5050State CurrentState { get; }

    /// <summary>Notification currently shown on the AGV screen; null when none or expired.</summary>
    ScreenNotification? ActiveNotification { get; }

    Task TriggerDisconnectAsync(int durationMs);

    // Lifecycle
    Task ConnectAsync();
    Task StartAsync();
    Task StopAsync();
    Task DisconnectAsync();

    // Control
    void SetPosition(double x, double y, double theta, string mapId);
    void SetSpeed(double speed);
    /// <summary>Cài trần tốc độ + gia tốc (m/s, m/s^2). Edge cho cao hơn vẫn bị kẹp bởi MaxSpeed.</summary>
    void SetMotionConfig(double maxSpeed, double acceleration, double deceleration);
    (double MaxSpeed, double Acceleration, double Deceleration) GetMotionConfig();
    /// <summary>Cài thời gian thực hiện action (ms).</summary>
    void SetActionsConfig(int liftDurationMs, int lowerDurationMs, int genericActionDurationMs, int loadStateDelayMs);
    (int LiftDurationMs, int LowerDurationMs, int GenericActionDurationMs, int LoadStateDelayMs) GetActionsConfig();
    void SetBatteryLevel(double level);
    /// <summary>Đặt thời gian sạc đầy 0-100% (giờ, float). AGV nạp tuyến tính 100/(giờ*60) %/phút.</summary>
    void SetBatteryChargeConfig(double fullChargeHours);
    double GetFullChargeHours();
    /// <summary>Đặt thời gian hoạt động liên tục 100-0% (giờ, float). AGV đang chạy tụt tuyến tính 100/(giờ*60) %/phút.</summary>
    void SetBatteryDrainConfig(double continuousOperatingHours);
    double GetContinuousOperatingHours();
    Task<bool> SetOperatingModeAsync(string mode);
    void SetHoldActions(bool enabled);
    void ClearOrderState();
    /// <summary>Giả lập nghẽn mạng: delay giây trước khi enqueue order MQTT vào actor (inbound đã log, state echo sau).</summary>
    void SetOrderEchoDelay(double delaySeconds);
    Task LiftAsync();
    Task LowerAsync();
    Task<int> ClearLoadsAsync();

    /// <summary>Recent MQTT order / instantActions / notifications payloads received from ACS (debug).</summary>
    IReadOnlyList<InboundMqttMessage> GetRecentInboundMessages();

    // Error management
    Task AddErrorAsync(string errorType, string errorLevel = "WARNING", string? description = null);
    Task<bool> ClearErrorAsync(string errorType);
    Task<int> ClearAllErrorsAsync();
    Task InjectErrorTemplateAsync(ErrorTemplate template);
    Task ClearEmergencyStopAsync();
    Task RestoreLocalizationAsync();
    IReadOnlyList<VdaError> GetErrors();
    bool HasErrors { get; }
    bool HasFatalErrors { get; }

    // Edge-aware movement
    /// <summary>Provide a map graph so the AGV uses per-edge maxSpeed instead of the default speed multiplier.</summary>
    void SetMapGraph(SimMapGraph? graph);

    /// <summary>Add a map graph for multi-map support. Graphs are stored by MapId.</summary>
    void AddMapGraph(SimMapGraph graph);
}
