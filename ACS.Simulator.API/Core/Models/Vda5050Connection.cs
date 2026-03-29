using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// VDA5050 Connection message
/// Published with retain flag, Last Will = CONNECTIONBROKEN
/// Topic: uagv//{manufacturer}/{serialNumber}/connection
/// </summary>
public class Vda5050Connection
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

    [JsonPropertyName("connectionState")]
    public string ConnectionState { get; set; } = VDA5050ConnectionState.Online;
}

public static class VDA5050ConnectionState
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string ConnectionBroken = "CONNECTIONBROKEN";
}
