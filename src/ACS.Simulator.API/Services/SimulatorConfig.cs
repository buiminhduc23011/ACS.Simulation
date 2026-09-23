namespace ACS.Simulator.API.Services;

public class SimulatorConfig
{
    public string MqttHost { get; set; } = "127.0.0.1";
    public int MqttPort { get; set; } = 1883;
    public string AcsApiBaseUrl { get; set; } = "http://localhost:9050";
}
