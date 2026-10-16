namespace ACS.Simulator.API.Services;

public class SimulatorConfig
{
    public string MqttHost { get; set; } = "127.0.0.1";
    public int MqttPort { get; set; } = 1883;
    public string AcsApiBaseUrl { get; set; } = "http://localhost:9050";

    /// <summary>Khoảng cách tối thiểu tới AGV khác (m) — dưới ngưỡng này luôn dừng.</summary>
    public double MinSeparationMeters { get; set; } = 1.0;

    /// <summary>Tầm dừng phía trước (m) khi AGV khác nằm trong nón trước mặt.</summary>
    public double FrontStopDistanceMeters { get; set; } = 1.5;

    /// <summary>Nửa góc nón trước khi di chuyển (độ).</summary>
    public double FrontConeHalfAngleDeg { get; set; } = 30.0;

    /// <summary>Nửa góc nón trước khi xoay tại chỗ (độ).</summary>
    public double RotateFrontConeHalfAngleDeg { get; set; } = 30.0;
}
