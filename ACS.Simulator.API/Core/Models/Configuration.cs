namespace ACS.Simulator.API.Core.Models;

public class AgvConfiguration
{
    public int AgvId { get; set; }
    public string Manufacturer { get; set; } = "stivn";
    public string SerialNumber { get; set; } = string.Empty;
    public string Component { get; set; } = "agv";
    public string IpAddress { get; set; } = "192.168.1.100";
    public string MacAddress { get; set; } = "00:00:00:00:00:00";
    public PositionInfo InitialPosition { get; set; } = new();
    public int VehicleShapeId { get; set; } = 1; // Default: CARRIER
    public int NavigationTechnologyId { get; set; } = 1; // Default: NATURAL
}

public class PositionInfo
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Theta { get; set; }
    public string MapId { get; set; } = "";
}

public class MqttBrokerConfig
{
    public string Address { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
}

public class MovementConfig
{
    public double Speed { get; set; } = 1.5; // m/s
    public int MovementUpdateInterval { get; set; } = 100; // ms - cập nhật vị trí
    public int VisualizationPublishInterval { get; set; } = 200; // ms - publish visualization khi di chuyển
    public double Tolerance { get; set; } = 0.1; // meters
}

public class StatePublishConfig
{
    public int PeriodicInterval { get; set; } = 3000; // ms - chu kỳ publish state bình thường
}

public class ActionsConfig
{
    public int LiftDurationMs { get; set; } = 2000;
    public int LowerDurationMs { get; set; } = 2000;
}

public class BatteryConfig
{
    /// <summary>Phần trăm pin tiêu hao mỗi mét di chuyển (default: 0.05%/m)</summary>
    public double DrainRatePerMeter { get; set; } = 0.05;

    /// <summary>Phần trăm pin tiêu hao mỗi phút khi idle (default: 0.01%/min)</summary>
    public double DrainRateIdlePerMinute { get; set; } = 0.01;

    /// <summary>Phần trăm pin nạp mỗi phút khi charging (default: 2.0%/min)</summary>
    public double ChargeRatePerMinute { get; set; } = 2.0;

    /// <summary>Mức pin ban đầu (default: 100%)</summary>
    public double InitialLevel { get; set; } = 100.0;

    /// <summary>Ngưỡng pin thấp - tự động thêm warning error (default: 15%)</summary>
    public double CriticalLevel { get; set; } = 15.0;

    /// <summary>Bật/tắt mô phỏng pin (default: true)</summary>
    public bool Enabled { get; set; } = true;
}

public class NetworkChaosConfig
{
    /// <summary>Độ trễ tối thiểu giả lập (ms, 0 = tắt)</summary>
    public int MinLatencyMs { get; set; } = 0;

    /// <summary>Độ trễ tối đa giả lập (ms)</summary>
    public int MaxLatencyMs { get; set; } = 0;

    /// <summary>Tỷ lệ mất gói tin (0-100%, 0 = tắt)</summary>
    public int PacketLossPercent { get; set; } = 0;
}

public class AgvSimulatorSettings
{
    public MqttBrokerConfig MqttBroker { get; set; } = new();
    public AgvConfiguration Agv { get; set; } = new();
    public MovementConfig Movement { get; set; } = new();
    public StatePublishConfig StatePublish { get; set; } = new();
    public ActionsConfig Actions { get; set; } = new();
    public BatteryConfig Battery { get; set; } = new();
    public NetworkChaosConfig NetworkChaos { get; set; } = new();
}
