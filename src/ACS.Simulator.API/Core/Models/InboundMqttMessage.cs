namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// Ring-buffer entry for ACS → AGV MQTT traffic (order / instantActions / notifications) used by the control debug panel.
/// </summary>
public sealed class InboundMqttMessage
{
    public DateTime Timestamp { get; init; }
    public string TopicType { get; init; } = string.Empty; // "order" | "instantActions" | "notifications"
    public string Topic { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public bool Accepted { get; init; }
    public string? Note { get; init; }
}
