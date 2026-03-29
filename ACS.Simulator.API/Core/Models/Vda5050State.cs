using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

public class Vda5050State
{
    [JsonPropertyName("headerId")]
    public int HeaderId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0.0";

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("orderId")]
    public string OrderId { get; set; } = string.Empty;

    [JsonPropertyName("orderUpdateId")]
    public int OrderUpdateId { get; set; }

    [JsonPropertyName("lastNodeId")]
    public string LastNodeId { get; set; } = string.Empty;

    [JsonPropertyName("lastNodeSequenceId")]
    public int LastNodeSequenceId { get; set; }

    [JsonPropertyName("driving")]
    public bool Driving { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("newBaseRequest")]
    public bool NewBaseRequest { get; set; }

    [JsonPropertyName("distanceSinceLastNode")]
    public double DistanceSinceLastNode { get; set; }

    [JsonPropertyName("operatingMode")]
    public string OperatingMode { get; set; } = "AUTOMATIC";

    [JsonPropertyName("nodeStates")]
    public List<NodeState> NodeStates { get; set; } = new();

    [JsonPropertyName("edgeStates")]
    public List<EdgeState> EdgeStates { get; set; } = new();

    [JsonPropertyName("agvPosition")]
    public AgvPosition AgvPosition { get; set; } = new();

    [JsonPropertyName("velocity")]
    public Velocity Velocity { get; set; } = new();

    [JsonPropertyName("loads")]
    public List<Load> Loads { get; set; } = new();

    [JsonPropertyName("actionStates")]
    public List<ActionState> ActionStates { get; set; } = new();

    [JsonPropertyName("batteryState")]
    public BatteryState BatteryState { get; set; } = new();

    [JsonPropertyName("errors")]
    public List<VdaError> Errors { get; set; } = new();

    [JsonPropertyName("safetyState")]
    public SafetyState SafetyState { get; set; } = new();
}

public class NodeState
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("sequenceId")]
    public int SequenceId { get; set; }

    [JsonPropertyName("released")]
    public bool Released { get; set; }

    [JsonPropertyName("nodePosition")]
    public NodePosition? NodePosition { get; set; }
}

public class EdgeState
{
    [JsonPropertyName("edgeId")]
    public string EdId { get; set; } = string.Empty;

    [JsonPropertyName("sequenceId")]
    public int SequenceId { get; set; }

    [JsonPropertyName("released")]
    public bool Released { get; set; }
}

public class AgvPosition
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("theta")]
    public double Theta { get; set; }

    [JsonPropertyName("mapId")]
    public string MapId { get; set; } = string.Empty;

    [JsonPropertyName("positionInitialized")]
    public bool PositionInitialized { get; set; } = true;

    [JsonPropertyName("localizationScore")]
    public double LocalizationScore { get; set; } = 1.0;

    [JsonPropertyName("deviationRange")]
    public double? DeviationRange { get; set; }
}

public class Velocity
{
    [JsonPropertyName("vx")]
    public double Vx { get; set; }

    [JsonPropertyName("vy")]
    public double Vy { get; set; }

    [JsonPropertyName("omega")]
    public double Omega { get; set; }
}

public class Load
{
    [JsonPropertyName("loadId")]
    public string LoadId { get; set; } = string.Empty;

    [JsonPropertyName("loadType")]
    public string LoadType { get; set; } = string.Empty;

    [JsonPropertyName("weight")]
    public double Weight { get; set; }
}

public class ActionState
{
    [JsonPropertyName("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = string.Empty;

    [JsonPropertyName("actionStatus")]
    public string ActionStatus { get; set; } = "WAITING";

    [JsonPropertyName("actionDescription")]
    public string? ActionDescription { get; set; }

    [JsonPropertyName("resultDescription")]
    public string? ResultDescription { get; set; }
}

public class BatteryState
{
    [JsonPropertyName("batteryCharge")]
    public double BatteryCharge { get; set; } = 100.0;

    [JsonPropertyName("batteryVoltage")]
    public double BatteryVoltage { get; set; } = 48.0;

    [JsonPropertyName("charging")]
    public bool Charging { get; set; }

    [JsonPropertyName("reach")]
    public int Reach { get; set; } = 10000;
}

public class VdaError
{
    [JsonPropertyName("errorType")]
    public string ErrorType { get; set; } = string.Empty;

    [JsonPropertyName("errorLevel")]
    public string ErrorLevel { get; set; } = "WARNING";

    [JsonPropertyName("errorDescription")]
    public string? ErrorDescription { get; set; }
}

public class SafetyState
{
    [JsonPropertyName("eStop")]
    public string EStop { get; set; } = "NONE";

    [JsonPropertyName("fieldViolation")]
    public bool FieldViolation { get; set; }
}
