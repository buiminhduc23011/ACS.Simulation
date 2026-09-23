using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// VDA5050 Visualization message - Dữ liệu visualization của AGV
/// </summary>
public class Vda5050Visualization
{
    [JsonPropertyName("headerId")]
    public int HeaderId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0.0";

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("serialNumber")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("agvPosition")]
    public AgvPosition? AgvPosition { get; set; }

    [JsonPropertyName("velocity")]
    public Velocity? Velocity { get; set; }
}
