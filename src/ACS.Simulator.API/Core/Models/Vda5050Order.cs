using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

// VDA5050 Models

public class Vda5050Order
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

    [JsonPropertyName("zoneSetId")]
    public string? ZoneSetId { get; set; }

    [JsonPropertyName("nodes")]
    public List<Node> Nodes { get; set; } = new();

    [JsonPropertyName("edges")]
    public List<Edge> Edges { get; set; } = new();
}

public class Node
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("sequenceId")]
    public int SequenceId { get; set; }

    [JsonPropertyName("released")]
    public bool Released { get; set; } = true;

    [JsonPropertyName("nodePosition")]
    public NodePosition? NodePosition { get; set; }

    [JsonPropertyName("actions")]
    public List<VdaAction> Actions { get; set; } = new();
}

public class Edge
{
    [JsonPropertyName("edgeId")]
    public string EdgeId { get; set; } = string.Empty;

    [JsonPropertyName("sequenceId")]
    public int SequenceId { get; set; }

    [JsonPropertyName("released")]
    public bool Released { get; set; } = true;

    [JsonPropertyName("startNodeId")]
    public string StartNodeId { get; set; } = string.Empty;

    [JsonPropertyName("endNodeId")]
    public string EndNodeId { get; set; } = string.Empty;

    [JsonPropertyName("maxSpeed")]
    public double? MaxSpeed { get; set; }

    [JsonPropertyName("orientation")]
    public double? Orientation { get; set; }

    [JsonPropertyName("orientationType")]
    public string? OrientationType { get; set; }

    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    [JsonPropertyName("rotationAllowed")]
    public bool? RotationAllowed { get; set; }

    [JsonPropertyName("maxRotationSpeed")]
    public double? MaxRotationSpeed { get; set; }

    [JsonPropertyName("length")]
    public double? Length { get; set; }

    [JsonPropertyName("actions")]
    public List<VdaAction> Actions { get; set; } = new();

    [JsonPropertyName("trajectory")]
    public Trajectory? Trajectory { get; set; }
}

public class Trajectory
{
    [JsonPropertyName("degree")]
    public int Degree { get; set; }

    [JsonPropertyName("knotVector")]
    public List<double> KnotVector { get; set; } = new();

    [JsonPropertyName("controlPoints")]
    public List<ControlPoint> ControlPoints { get; set; } = new();
}

public class ControlPoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1.0;
}
public class NodePosition
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("theta")]
    public double? Theta { get; set; }

    [JsonPropertyName("mapId")]
    public string MapId { get; set; } = string.Empty;
}

public class VdaAction
{
    [JsonPropertyName("actionType")]
    public string ActionType { get; set; } = string.Empty;

    [JsonPropertyName("actionId")]
    public string ActionId { get; set; } = string.Empty;

    [JsonPropertyName("actionDescription")]
    public string? ActionDescription { get; set; }

    [JsonPropertyName("blockingType")]
    public string BlockingType { get; set; } = "HARD";

    [JsonPropertyName("actionParameters")]
    public List<ActionParameter>? ActionParameters { get; set; }
}

public class ActionParameter
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

/// <summary>
/// VDA5050 InstantActions message - sent by ACS to trigger immediate actions
/// Topic: uagv//{manufacturer}/{serialNumber}/instantActions
/// </summary>
public class Vda5050InstantActions
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

    [JsonPropertyName("actions")]
    public required List<VdaAction> Actions { get; set; }
}

/// <summary>
/// Instant action types supported by the simulator.
/// Action names are treated case-insensitively, but only canonical VDA5050 values are accepted.
/// </summary>
public static class InstantActionType
{
    public const string StartPause = "startPause";
    public const string StopPause = "stopPause";
    public const string CancelOrder = "cancelOrder";
    public const string StartCharging = "startCharging";
    public const string InitPosition = "initPosition";
    public const string InitializePosition = "initializePosition";
    public const string Pick = "pick";
    public const string Drop = "drop";
    public const string Lift = "lift";
    public const string Lower = "lower";
}

/// <summary>
/// ACS-defined node actions that the simulator recognizes for observability only.
/// They intentionally do not change any simulated vehicle or LiDAR state.
/// </summary>
public static class AcsNodeActionType
{
    public const string DisableSafetyFront = "acs.disableSafetyFront";
    public const string DisableSafetyRear = "acs.disableSafetyRear";
}
