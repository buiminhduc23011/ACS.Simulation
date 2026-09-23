using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using System.Threading.Channels;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Virtual AGV - Simulates a real AGV with movement and action execution capabilities.
/// Subscribe format: uagv/{manufacturer}/{serialNumber}/order
///                   uagv/{manufacturer}/{serialNumber}/instantActions
/// Publish state:    3s periodic cycle + immediately on state change
/// Publish viz:      configurable interval while moving
/// Publish connect:  ONLINE on connect, OFFLINE on disconnect, Last Will = CONNECTIONBROKEN
///
/// This file contains: fields, properties, topics, and constructor.
/// Behaviour is split across partial class files:
///   VirtualAgv.Mqtt.cs          — MQTT connect / subscribe / publish / network chaos
///   VirtualAgv.Lifecycle.cs     — Startup / shutdown / actor event loop / timer helpers
///   VirtualAgv.OrderProcessing.cs — VDA5050 order execution, instant actions
///   VirtualAgv.Movement.cs      — Movement physics, heading, trajectory, collision
///   VirtualAgv.State.cs         — State publish, visualization, battery, order-state sync
///   VirtualAgv.Actions.cs       — PICK/DROP/WAIT, lift/lower, error management
///   VirtualAgv.ManualControl.cs — Joystick position, map graph, public command APIs
/// </summary>
public partial class VirtualAgv : IVirtualAgv
{
    private readonly ILogger<VirtualAgv> _logger;
    private readonly AgvConfiguration _config;
    private readonly MqttBrokerConfig _mqttConfig;
    private readonly MovementConfig _movementConfig;
    private readonly StatePublishConfig _statePublishConfig;
    private readonly ActionsConfig _actionsConfig;
    private readonly BatteryConfig _batteryConfig;
    private readonly IMqttClient _mqttClient;

    // Network chaos state
    private int _chaosMinLatencyMs = 0;
    private int _chaosMaxLatencyMs = 0;
    private int _chaosPacketLossPercent = 0;
    private readonly Random _chaosRandom = new();

    // Battery simulation
    private double _batteryLevel;
    private bool _isCharging = false;
    private DateTime _lastIdleDrainTime = DateTime.UtcNow;
    private bool _batteryWarningActive = false;

    // Edge-aware movement: map graphs for per-edge maxSpeed (supports multi-map)
    private SimMapGraph? _mapGraph;
    private readonly Dictionary<string, SimMapGraph> _mapGraphs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mapIdMappings = new(StringComparer.OrdinalIgnoreCase);
    private string _prevNodeId = string.Empty;
    private double _currentEdgeMaxSpeed = 0.0; // 0 = use default

    private Vda5050State _currentState;
    private Vda5050Visualization _currentVisualization;
    private Vda5050Order? _currentOrder;
    private int _headerIdCounter = 0;
    private int GetNextHeaderId() => Interlocked.Increment(ref _headerIdCounter);
    private Timer? _statePublishTimer;
    private Timer? _movementTimer;
    private Timer? _visualizationTimer;
    private bool _isRunning = false;
    private bool _disposed = false;

    // Actor-loop: all state mutations serialized through bounded channel
    private readonly Channel<AgvCommand> _commandChannel;
    private CancellationTokenSource? _loopCts;
    private Task? _eventLoopTask;
    private CancellationTokenSource? _chaosReconnectCts;
    private Task? _chaosReconnectTask;
    private bool _mqttHandlerAttached;

    // Manual position (joystick) control: auto-stop visualization after inactivity
    private Timer? _manualPositionTimer;
    private DateTime _lastManualPositionUpdate = DateTime.MinValue;
    private static readonly TimeSpan ManualPositionTimeout = TimeSpan.FromMilliseconds(500);
    private const double MovementHeadingAlignmentTolerance = Math.PI / 4.0;
    private const double ManualVelocityEpsilon = 1e-3;

    // Movement state
    private double _currentX;
    private double _currentY;
    private double _currentTheta;
    private string _currentMapId;
    private double _targetX;
    private double _targetY;
    private double _targetTheta; // Desired body heading while moving
    private double? _arrivalTheta;
    private bool _isReversing;
    private bool _isMoving = false;
    private bool _isRotating = false;
    private int _currentNodeIndex = -1;

    // Curved trajectory state
    private List<(double X, double Y)>? _activeTrajectoryPoints;
    private int _currentTrajectorySegmentIndex = 0;

    private Func<string, double, double, double, double, double, bool, string, string?>? _movementBlockResolver;
    private string? _currentMovementBlocker;
    private DateTime _lastBlockLogAt = DateTime.MinValue;
    private DateTime _lastManualPoseAt = DateTime.MinValue;

    // Action state
    private bool _hasLoad = false;
    private CancellationTokenSource? _actionCts;
    // Latched immediately on MQTT startPause so movement stops even while the event loop
    // is blocked inside a long-running pick/drop. stopPause clears it.
    private volatile bool _pauseLatched;

    // ACS inbound MQTT debug ring buffer (order + instantActions)
    private const int MaxInboundMqttMessages = 50;
    private readonly object _inboundMessagesLock = new();
    private readonly LinkedList<InboundMqttMessage> _inboundMessages = new();

    // Topics following custom format: uagv/{manufacturer}/{serialNumber}/{topicType}
    private string StateTopic         => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/state";
    private string VisualizationTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/visualization";
    private string OrderTopic         => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/order";
    private string ConnectionTopic    => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/connection";
    private string InstantActionsTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/instantActions";

    public string SerialNumber => _config.SerialNumber;
    public string IpAddress    => _config.IpAddress;
    public string MacAddress   => _config.MacAddress;
    public bool IsConnected    => _mqttClient?.IsConnected ?? false;
    public bool IsRunning      => _isRunning;
    public Vda5050State CurrentState => _currentState;

    public VirtualAgv(
        AgvConfiguration config,
        MqttBrokerConfig mqttConfig,
        MovementConfig movementConfig,
        StatePublishConfig statePublishConfig,
        ActionsConfig actionsConfig,
        ILogger<VirtualAgv> logger,
        BatteryConfig? batteryConfig = null,
        NetworkChaosConfig? networkChaosConfig = null)
    {
        _config = config;
        _mqttConfig = mqttConfig;
        _movementConfig = movementConfig;
        _statePublishConfig = statePublishConfig;
        _actionsConfig = actionsConfig;
        _batteryConfig = batteryConfig ?? new BatteryConfig();
        _logger = logger;

        // Initialize battery
        _batteryLevel = _batteryConfig.InitialLevel;

        // Initialize network chaos
        if (networkChaosConfig != null)
        {
            _chaosMinLatencyMs = networkChaosConfig.MinLatencyMs;
            _chaosMaxLatencyMs = networkChaosConfig.MaxLatencyMs;
            _chaosPacketLossPercent = networkChaosConfig.PacketLossPercent;
        }

        // Initialize position
        foreach (var mapping in config.MapMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceMapId) || string.IsNullOrWhiteSpace(mapping.TargetMapId))
            {
                continue;
            }

            _mapIdMappings[mapping.SourceMapId.Trim()] = mapping.TargetMapId.Trim();
        }

        _currentX = config.InitialPosition.X;
        _currentY = config.InitialPosition.Y;
        _currentTheta = config.InitialPosition.Theta;
        _targetTheta = _currentTheta;
        _currentMapId = ResolveInboundMapId(config.InitialPosition.MapId);

        // Initialize state
        _currentState = new Vda5050State
        {
            HeaderId = 0,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Version = "2.0.0",
            Manufacturer = config.Manufacturer,
            SerialNumber = config.SerialNumber,
            AgvPosition = new AgvPosition
            {
                X = _currentX,
                Y = _currentY,
                Theta = NormalizeAngle(_currentTheta),
                MapId = _currentMapId,
                PositionInitialized = true
            },
            Velocity = new Velocity { Vx = 0, Vy = 0, Omega = 0 },
            BatteryState = new BatteryState
            {
                BatteryCharge = _batteryConfig.InitialLevel,
                BatteryVoltage = 48.0,
                Charging = false,
                Reach = CalculateReach(_batteryConfig.InitialLevel)
            },
            OperatingMode = "AUTOMATIC",
            Driving = false,
            Paused = false,
            NewBaseRequest = false,
            DistanceSinceLastNode = 0,
            LastNodeId = ResolveNearestNodeIdOnCurrentMap() ?? string.Empty,
            LastNodeSequenceId = 0,
            OrderId = "",
            OrderUpdateId = 0,
            SafetyState = new SafetyState
            {
                EStop = "NONE",
                FieldViolation = false
            },
            Loads = new List<Load>(),
            ActionStates = new List<ActionState>(),
            NodeStates = new List<NodeState>(),
            EdgeStates = new List<EdgeState>(),
            Errors = new List<VdaError>()
        };

        // Initialize visualization
        _currentVisualization = new Vda5050Visualization
        {
            HeaderId = 0,
            Timestamp = DateTime.UtcNow,
            Version = "2.0.0",
            Manufacturer = config.Manufacturer,
            SerialNumber = config.SerialNumber,
            AgvPosition = new AgvPosition
            {
                X = _currentX,
                Y = _currentY,
                Theta = NormalizeAngle(_currentTheta),
                MapId = _currentMapId,
                PositionInitialized = true
            },
            Velocity = new Velocity { Vx = 0, Vy = 0, Omega = 0 }
        };

        // Create MQTT client
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();

        // Bounded channel for actor loop; never drop commands (especially ProcessOrderCmd).
        _commandChannel = Channel.CreateBounded<AgvCommand>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
    }

    private int CalculateReach(double batteryLevel)
    {
        if (!_batteryConfig.Enabled || _batteryConfig.DrainRatePerMeter <= 0)
            return 10000;
            
        return (int)(batteryLevel / _batteryConfig.DrainRatePerMeter);
    }
}
