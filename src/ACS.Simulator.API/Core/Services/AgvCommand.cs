using ACS.Simulator.API.Core.Models;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Discriminated union of commands processed by VirtualAgv event loop.
/// All state mutations are serialized through this channel — zero race conditions.
/// </summary>
public abstract record AgvCommand;

/// <summary>Fired by _movementTimer every 100ms to advance position.</summary>
public sealed record MovementTickCmd : AgvCommand
{
    public static readonly MovementTickCmd Instance = new();
}

/// <summary>Fired by _statePublishTimer (periodic) or explicitly (immediate).</summary>
public sealed record StatePublishTickCmd(bool Immediate) : AgvCommand;

/// <summary>Fired by _visualizationTimer every 200ms while moving.</summary>
public sealed record VisualizationTickCmd : AgvCommand
{
    public static readonly VisualizationTickCmd Instance = new();
}

/// <summary>Enqueued by MQTT OnMessageReceived for VDA5050 order topic.</summary>
public sealed record ProcessOrderCmd(Vda5050Order Order) : AgvCommand;

/// <summary>Enqueued by MQTT OnMessageReceived for VDA5050 instantActions topic.</summary>
public sealed record ProcessInstantActionsCmd(Vda5050InstantActions Actions) : AgvCommand;

/// <summary>Enqueued by SetPosition() HTTP call.</summary>
public sealed record SetPositionCmd(double X, double Y, double Theta, string MapId) : AgvCommand;

/// <summary>Enqueued by SetBatteryLevel() HTTP call.</summary>
public sealed record SetBatteryCmd(double Level) : AgvCommand;

/// <summary>Enqueued by SetSpeed() HTTP call.</summary>
public sealed record SetSpeedCmd(double Speed) : AgvCommand;

/// <summary>Enqueued by SetChaosLatency() + SetPacketLoss() HTTP calls.</summary>
public sealed record SetChaosCmd(int MinMs, int MaxMs, int LossPercent) : AgvCommand;

/// <summary>Enqueued by AddErrorAsync().</summary>
public sealed record AddErrorCmd(string ErrorType, string ErrorLevel, string? Description) : AgvCommand;

/// <summary>Enqueued by ClearErrorAsync() and ClearAllErrorsAsync(). Null = clear all.</summary>
public sealed record ClearErrorCmd(string? ErrorType) : AgvCommand;

/// <summary>Enqueued by InjectErrorTemplateAsync().</summary>
public sealed record InjectErrorTemplateCmd(ErrorTemplate Template) : AgvCommand;

/// <summary>Enqueued by LiftAsync() manual control.</summary>
public sealed record LiftCmd : AgvCommand;

/// <summary>Enqueued by LowerAsync() manual control.</summary>
public sealed record LowerCmd : AgvCommand;

/// <summary>Enqueued by ClearLoadsAsync() manual control.</summary>
public sealed record ClearLoadsCmd(TaskCompletionSource<int> Completion) : AgvCommand;

/// <summary>Enqueued by ClearEmergencyStopAsync().</summary>
public sealed record ClearEmergencyStopCmd : AgvCommand;

/// <summary>Enqueued by RestoreLocalizationAsync().</summary>
public sealed record RestoreLocalizationCmd : AgvCommand;

/// <summary>Enqueued by manual position watchdog when it detects inactivity.</summary>
public sealed record ManualPositionStopCmd : AgvCommand
{
    public static readonly ManualPositionStopCmd Instance = new();
}

/// <summary>Enqueued by SetOperatingMode() HTTP call (AUTOMATIC / MANUAL).</summary>
public sealed record SetOperatingModeCmd(string Mode) : AgvCommand;

/// <summary>Enqueued by ClearOrderState() HTTP call.</summary>
public sealed record ClearOrderStateCmd() : AgvCommand;
