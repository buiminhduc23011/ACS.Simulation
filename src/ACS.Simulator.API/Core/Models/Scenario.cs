using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// VDA5050 AGV Simulator - Scenario Scripting
/// Cho phép định nghĩa chuỗi bước test tự động.
/// </summary>
public class Scenario
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("steps")]
    public List<ScenarioStep> Steps { get; set; } = new();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WaitStep), nameof(StepType.Wait))]
[JsonDerivedType(typeof(InjectErrorStep), nameof(StepType.InjectError))]
[JsonDerivedType(typeof(ClearErrorStep), nameof(StepType.ClearError))]
[JsonDerivedType(typeof(DisconnectStep), nameof(StepType.Disconnect))]
[JsonDerivedType(typeof(SetBatteryStep), nameof(StepType.SetBattery))]
[JsonDerivedType(typeof(SetPositionStep), nameof(StepType.SetPosition))]
[JsonDerivedType(typeof(SetSpeedStep), nameof(StepType.SetSpeed))]
[JsonDerivedType(typeof(AssertStep), nameof(StepType.Assert))]
[JsonDerivedType(typeof(LogStep), nameof(StepType.Log))]
[JsonDerivedType(typeof(SetChaosStep), nameof(StepType.SetChaos))]
public abstract class ScenarioStep
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}

public static class StepType
{
    public const string Wait = "Wait";
    public const string InjectError = "InjectError";
    public const string ClearError = "ClearError";
    public const string Disconnect = "Disconnect";
    public const string SetBattery = "SetBattery";
    public const string SetPosition = "SetPosition";
    public const string SetSpeed = "SetSpeed";
    public const string Assert = "Assert";
    public const string Log = "Log";
    public const string SetChaos = "SetChaos";
}

public class WaitStep : ScenarioStep
{
    public override string Type => StepType.Wait;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; } = 1000;
}

public class InjectErrorStep : ScenarioStep
{
    public override string Type => StepType.InjectError;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("templateName")]
    public string TemplateName { get; set; } = string.Empty;
}

public class ClearErrorStep : ScenarioStep
{
    public override string Type => StepType.ClearError;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    /// <summary>null = clear all, otherwise clear by errorType</summary>
    [JsonPropertyName("errorType")]
    public string? ErrorType { get; set; }
}

public class DisconnectStep : ScenarioStep
{
    public override string Type => StepType.Disconnect;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; } = 5000;
}

public class SetBatteryStep : ScenarioStep
{
    public override string Type => StepType.SetBattery;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public double Level { get; set; } = 100.0;
}

public class SetPositionStep : ScenarioStep
{
    public override string Type => StepType.SetPosition;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("theta")]
    public double Theta { get; set; }

    [JsonPropertyName("mapId")]
    public string MapId { get; set; } = "";
}

public class SetSpeedStep : ScenarioStep
{
    public override string Type => StepType.SetSpeed;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("speed")]
    public double Speed { get; set; } = 1.0;
}

/// <summary>
/// Assert step - verify AGV state condition.
/// Condition format: "battery < 20", "driving == true", "isConnected == true", "errorCount > 0"
/// </summary>
public class AssertStep : ScenarioStep
{
    public override string Type => StepType.Assert;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = string.Empty;

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; set; } = 5000;
}

public class LogStep : ScenarioStep
{
    public override string Type => StepType.Log;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class SetChaosStep : ScenarioStep
{
    public override string Type => StepType.SetChaos;

    [JsonPropertyName("agvId")]
    public string AgvId { get; set; } = string.Empty;

    [JsonPropertyName("latencyMinMs")]
    public int LatencyMinMs { get; set; } = 0;

    [JsonPropertyName("latencyMaxMs")]
    public int LatencyMaxMs { get; set; } = 0;

    [JsonPropertyName("packetLossPercent")]
    public int PacketLossPercent { get; set; } = 0;
}

/// <summary>Result of running one scenario step</summary>
public record ScenarioStepResult(
    string StepType,
    string? Comment,
    bool Passed,
    string? Message,
    TimeSpan Duration);

/// <summary>Result of running a full scenario</summary>
public record ScenarioRunResult(
    string ScenarioName,
    DateTime StartTime,
    DateTime EndTime,
    List<ScenarioStepResult> StepResults)
{
    public int TotalSteps => StepResults.Count;
    public int PassedSteps => StepResults.Count(r => r.Passed);
    public int FailedSteps => StepResults.Count(r => !r.Passed);
    public bool Passed => FailedSteps == 0;
    public TimeSpan TotalDuration => EndTime - StartTime;
}
