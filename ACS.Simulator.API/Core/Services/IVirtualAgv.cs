using ACS.Simulator.API.Core.Models;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Interface for Virtual AGV - allows mocking in tests and scenario scripting.
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
    Vda5050State CurrentState { get; }

    // Network chaos
    void SetChaosLatency(int minMs, int maxMs);
    void SetPacketLoss(int percent);
    Task TriggerDisconnectAsync(int durationMs);

    // Lifecycle
    Task ConnectAsync();
    Task StartAsync();
    Task StopAsync();
    Task DisconnectAsync();

    // Control
    void SetPosition(double x, double y, double theta, string mapId);
    void SetSpeed(double speed);
    void SetBatteryLevel(double level);
    Task LiftAsync();
    Task LowerAsync();

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
