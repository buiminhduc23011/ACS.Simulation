using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Virtual AGV - Simulates a real AGV with movement and action execution capabilities
/// Subscribe format: uagv//{manufacturer}/{serialNumber}/order
///                   uagv//{manufacturer}/{serialNumber}/instantActions
/// Publish state: 3s periodic cycle + immediately on state change
/// Publish visualization: 200ms while moving
/// Publish connection: ONLINE on connect, OFFLINE on disconnect, Last Will = CONNECTIONBROKEN
/// </summary>
public class VirtualAgv : IVirtualAgv
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
    private string _prevNodeId = string.Empty;
    private double _currentEdgeMaxSpeed = 0.0; // 0 = use default

    private Vda5050State _currentState;
    private Vda5050Visualization _currentVisualization;
    private Vda5050Order? _currentOrder;
    private int _headerIdCounter = 0;
    private Timer? _statePublishTimer;
    private Timer? _movementTimer;
    private Timer? _visualizationTimer;
    private bool _isRunning = false;
    private bool _disposed = false;

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
    private bool _isRotating = false; // Track if AGV is rotating
    private int _currentNodeIndex = -1;
    private Func<string, double, double, double, double, double, bool, string, string?>? _movementBlockResolver;
    private string? _currentMovementBlocker;
    private DateTime _lastBlockLogAt = DateTime.MinValue;
    private DateTime _lastManualPoseAt = DateTime.MinValue;

    // Action state
    private bool _hasLoad = false;
    private CancellationTokenSource? _actionCts;

    // Topics following custom format: uagv/{manufacturer}/{serialNumber}/{topicType}
    private string StateTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/state";
    private string VisualizationTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/visualization";
    private string OrderTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/order";
    private string ConnectionTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/connection";
    private string InstantActionsTopic => $"uagv/v2/{_config.Manufacturer}/{_config.SerialNumber}/instantActions";

    public string SerialNumber => _config.SerialNumber;
    public string IpAddress => _config.IpAddress;
    public string MacAddress => _config.MacAddress;
    public bool IsConnected => _mqttClient?.IsConnected ?? false;
    public bool IsRunning => _isRunning;
    public Vda5050State CurrentState => _currentState;

    /// <inheritdoc/>
    public void SetMapGraph(SimMapGraph? graph)
    {
        _mapGraph = graph;
        if (graph != null && !string.IsNullOrEmpty(graph.MapId))
        {
            _mapGraphs[graph.MapId] = graph;
        }
        _logger.LogInformation("AGV {SerialNumber} map graph updated: {NodeCount} nodes, {EdgeCount} edges (MapId={MapId})",
            _config.SerialNumber,
            graph?.Nodes.Count ?? 0,
            graph?.Adjacency.Values.Sum(e => e.Count) ?? 0,
            graph?.MapId ?? "null");
    }

    /// <summary>
    /// Add or replace a map graph for a specific map. Supports multi-map operation.
    /// </summary>
    public void AddMapGraph(SimMapGraph graph)
    {
        if (graph == null || string.IsNullOrEmpty(graph.MapId)) return;
        _mapGraphs[graph.MapId] = graph;
        // Also set as primary if no primary graph yet
        _mapGraph ??= graph;
        _logger.LogInformation("AGV {SerialNumber} added map graph for MapId={MapId} ({NodeCount} nodes)",
            _config.SerialNumber, graph.MapId, graph.Nodes.Count);
    }

    /// <summary>
    /// Set movement safety resolver to block motion when another AGV is too close/in front.
    /// </summary>
    public void SetMovementBlockResolver(
        Func<string, double, double, double, double, double, bool, string, string?> resolver)
    {
        _movementBlockResolver = resolver;
    }

    /// <summary>
    /// Find edge from the appropriate map graph (cross-map aware).
    /// </summary>
    private SimEdgeDto? FindEdgeAcrossMaps(string fromNodeId, string toNodeId)
    {
        // First try the map-specific graph for the current map
        if (!string.IsNullOrEmpty(_currentMapId) && _mapGraphs.TryGetValue(_currentMapId, out var currentGraph))
        {
            var edge = currentGraph.FindEdge(fromNodeId, toNodeId);
            if (edge != null) return edge;
        }

        // Then try the primary graph
        var primaryEdge = _mapGraph?.FindEdge(fromNodeId, toNodeId);
        if (primaryEdge != null) return primaryEdge;

        // Finally, try all loaded graphs
        foreach (var graph in _mapGraphs.Values)
        {
            var edge = graph.FindEdge(fromNodeId, toNodeId);
            if (edge != null) return edge;
        }

        return null;
    }

    private double GetCurrentEdgeMaxSpeed()
    {
        if (string.IsNullOrEmpty(_prevNodeId) || _currentNodeIndex < 0
            || _currentOrder == null || _currentNodeIndex >= _currentOrder.Nodes.Count)
            return 0.0;

        var targetNodeId = _currentOrder.Nodes[_currentNodeIndex].NodeId;
        var edge = FindEdgeAcrossMaps(_prevNodeId, targetNodeId);
        return edge?.MaxSpeed ?? 0.0;
    }

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
        _currentX = config.InitialPosition.X;
        _currentY = config.InitialPosition.Y;
        _currentTheta = config.InitialPosition.Theta;
        _targetTheta = _currentTheta;
        _currentMapId = config.InitialPosition.MapId;

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
                Theta = ToServerTheta(_currentTheta),
                MapId = _currentMapId,
                PositionInitialized = true
            },
            Velocity = new Velocity { Vx = 0, Vy = 0, Omega = 0 },
            BatteryState = new BatteryState
            {
                BatteryCharge = _batteryConfig.InitialLevel,
                BatteryVoltage = 48.0,
                Charging = false
            },
            OperatingMode = "AUTOMATIC",
            Driving = false,
            Paused = false,
            NewBaseRequest = false,
            DistanceSinceLastNode = 0,
            LastNodeId = "0", // Initialize with default value
            LastNodeSequenceId = 0,
            OrderId = "", // Initialize empty
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
                Theta = ToServerTheta(_currentTheta),
                MapId = _currentMapId,
                PositionInitialized = true
            },
            Velocity = new Velocity { Vx = 0, Vy = 0, Omega = 0 }
        };

        // Create MQTT client
        var factory = new MqttFactory();
        _mqttClient = factory.CreateMqttClient();
    }

    public async Task ConnectAsync()
    {
        try
        {
            // Prepare Last Will message (CONNECTIONBROKEN) - published by broker if client disconnects ungracefully
            var lastWillPayload = JsonSerializer.Serialize(new Vda5050Connection
            {
                HeaderId = 0,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Version = "2.0.0",
                Manufacturer = _config.Manufacturer,
                SerialNumber = _config.SerialNumber,
                ConnectionState = VDA5050ConnectionState.ConnectionBroken
            });

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
                .WithClientId($"AGV_{_config.MacAddress.Replace(":", "")}")
                .WithCleanSession()
                .WithWillTopic(ConnectionTopic)
                .WithWillPayload(Encoding.UTF8.GetBytes(lastWillPayload))
                .WithWillRetain(true)
                .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += OnMessageReceived;

            await _mqttClient.ConnectAsync(options);
            _logger.LogInformation("AGV {SerialNumber} connected to MQTT broker at {Broker}:{Port}",
                _config.SerialNumber, _mqttConfig.Address, _mqttConfig.Port);

            // Subscribe to order topic with explicit QoS for reliable delivery.
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(OrderTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build());
            _logger.LogInformation("AGV {SerialNumber} subscribed to order topic: {Topic}", _config.SerialNumber, OrderTopic);

            // Subscribe to instantActions topic with explicit QoS.
            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                .WithTopic(InstantActionsTopic)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
                .Build());
            _logger.LogInformation("AGV {SerialNumber} subscribed to instantActions topic: {Topic}", _config.SerialNumber, InstantActionsTopic);

            // Publish ONLINE connection state (retain=true)
            await PublishConnectionStateAsync(VDA5050ConnectionState.Online);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect AGV {SerialNumber} to MQTT broker", _config.SerialNumber);
            throw;
        }
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        _isRunning = true;

        // Start publishing state periodically - 3s cycle
        _statePublishTimer = new Timer(async _ => await PublishStateAsync(false), null,
            TimeSpan.FromMilliseconds(_statePublishConfig.PeriodicInterval),
            TimeSpan.FromMilliseconds(_statePublishConfig.PeriodicInterval));

        // Start movement update timer - 100ms
        _movementTimer = new Timer(async _ => await UpdateMovementAsync(), null,
            TimeSpan.FromMilliseconds(_movementConfig.MovementUpdateInterval),
            TimeSpan.FromMilliseconds(_movementConfig.MovementUpdateInterval));

        _logger.LogInformation("AGV {SerialNumber} started - State publish: {StatePeriod}ms, Movement update: {MovementPeriod}ms",
            _config.SerialNumber, _statePublishConfig.PeriodicInterval, _movementConfig.MovementUpdateInterval);
    }

    private async Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

            if (topic == OrderTopic)
            {
                _logger.LogInformation("AGV {SerialNumber} received order on topic {Topic}",
                    _config.SerialNumber, topic);
                var order = JsonSerializer.Deserialize<Vda5050Order>(payload);
                if (order != null)
                    await ProcessOrderAsync(order);
            }
            else if (topic == InstantActionsTopic)
            {
                _logger.LogInformation("AGV {SerialNumber} received instantActions on topic {Topic}",
                    _config.SerialNumber, topic);
                var instantActions = JsonSerializer.Deserialize<Vda5050InstantActions>(payload);
                if (instantActions != null)
                    await ProcessInstantActionsAsync(instantActions);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    private async Task ProcessInstantActionsAsync(Vda5050InstantActions instantActions)
    {
        foreach (var action in instantActions.Actions)
        {
            _logger.LogInformation("AGV {SerialNumber} processing instant action: {ActionType} (id: {ActionId})",
                _config.SerialNumber, action.ActionType, action.ActionId);

            var actionState = new ActionState
            {
                ActionId = action.ActionId,
                ActionType = action.ActionType,
                ActionStatus = "RUNNING"
            };
            _currentState.ActionStates.Add(actionState);

            try
            {
                var normalizedActionType = NormalizeInstantActionType(action.ActionType);
                switch (normalizedActionType)
                {
                    case InstantActionType.Pause:
                    case InstantActionType.StartPause:
                        _currentState.Paused = true;
                        _isMoving = false;
                        _currentState.Driving = false;
                        StopVisualizationTimer();
                        _logger.LogInformation("AGV {SerialNumber} PAUSED via instant action", _config.SerialNumber);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.Resume:
                    case InstantActionType.StopPause:
                        _currentState.Paused = false;
                        _logger.LogInformation("AGV {SerialNumber} RESUMED via instant action", _config.SerialNumber);
                        // Resume movement if there's a current order in progress
                        if (_currentOrder != null && _currentNodeIndex >= 0)
                        {
                            _isMoving = true;
                            _currentState.Driving = true;
                            StartVisualizationTimer();
                        }
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.CancelOrder:
                        actionState.ActionStatus = "FINISHED";
                        actionState.ResultDescription = "Order cancelled";
                        ResetStateForCancelledOrder(actionState);
                        _logger.LogInformation("AGV {SerialNumber} order CANCELLED via instant action", _config.SerialNumber);
                        break;

                    case InstantActionType.StartCharging:
                        _isCharging = true;
                        _currentState.BatteryState.Charging = true;
                        _currentState.OperatingMode = "AUTOMATIC";
                        _logger.LogInformation("AGV {SerialNumber} started CHARGING (battery: {Level:F1}%)",
                            _config.SerialNumber, _batteryLevel);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.StopCharging:
                        _isCharging = false;
                        _currentState.BatteryState.Charging = false;
                        _logger.LogInformation("AGV {SerialNumber} stopped CHARGING (battery: {Level:F1}%)",
                            _config.SerialNumber, _batteryLevel);
                        actionState.ActionStatus = "FINISHED";
                        break;

                    case InstantActionType.InitPosition:
                        var xParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "x");
                        var yParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "y");
                        var thetaParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "theta");
                        var mapParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "mapId");
                        if (xParam != null && yParam != null)
                        {
                            double ix = Convert.ToDouble(xParam.Value);
                            double iy = Convert.ToDouble(yParam.Value);
                            double ith = thetaParam != null ? Convert.ToDouble(thetaParam.Value) : 0;
                            string imId = mapParam?.Value?.ToString() ?? _currentMapId;
                            SetPosition(ix, iy, ith, imId);
                        }
                        _currentState.AgvPosition!.PositionInitialized = true;
                        _currentState.AgvPosition.LocalizationScore = 1.0;
                        actionState.ActionStatus = "FINISHED";
                        break;

                    default:
                        _logger.LogWarning("AGV {SerialNumber} unknown instant action type: {ActionType}",
                            _config.SerialNumber, action.ActionType);
                        actionState.ActionStatus = "FAILED";
                        actionState.ResultDescription = $"Unsupported instant action type '{action.ActionType}'";
                        break;
                }
            }
            catch (Exception ex)
            {
                actionState.ActionStatus = "FAILED";
                actionState.ResultDescription = ex.Message;
                _logger.LogError(ex, "AGV {SerialNumber} failed instant action {ActionType}", _config.SerialNumber, action.ActionType);
            }
        }

        await PublishStateAsync(true);
    }

    private static string NormalizeInstantActionType(string? actionType)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            return string.Empty;
        }

        var trimmed = actionType.Trim();
        if (trimmed.Equals(InstantActionType.Pause, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.Pause;
        }

        if (trimmed.Equals(InstantActionType.Resume, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.Resume;
        }

        if (trimmed.Equals(InstantActionType.CancelOrder, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.CancelOrder;
        }

        if (trimmed.Equals(InstantActionType.StartCharging, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.StartCharging;
        }

        if (trimmed.Equals(InstantActionType.StopCharging, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.StopCharging;
        }

        if (trimmed.Equals(InstantActionType.InitPosition, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.InitPosition;
        }

        if (trimmed.Equals(InstantActionType.StartPause, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.StartPause;
        }

        if (trimmed.Equals(InstantActionType.StopPause, StringComparison.OrdinalIgnoreCase))
        {
            return InstantActionType.StopPause;
        }

        return trimmed;
    }

    private void ResetStateForCancelledOrder(ActionState cancelActionState)
    {
        _currentOrder = null;
        _currentNodeIndex = -1;
        _isMoving = false;
        _isRotating = false;
        _isReversing = false;
        _arrivalTheta = null;
        _targetTheta = _currentTheta;
        _currentMovementBlocker = null;

        _currentState.Driving = false;
        _currentState.Paused = false;
        _currentState.OrderId = "";
        _currentState.OrderUpdateId = 0;
        _currentState.NewBaseRequest = false;
        _currentState.NodeStates.Clear();
        _currentState.EdgeStates.Clear();
        _currentState.ActionStates.Clear();
        _currentState.ActionStates.Add(cancelActionState);
        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;

        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;

        StopVisualizationTimer();
        _actionCts?.Cancel();
        _actionCts = null;
    }

    private async Task ProcessOrderAsync(Vda5050Order order)
    {
        if (_currentOrder != null)
        {
            if (_currentOrder.OrderId == order.OrderId &&
                order.OrderUpdateId <= _currentOrder.OrderUpdateId)
            {
                _logger.LogInformation(
                    "AGV {SerialNumber} ignored stale/duplicate order message: orderId={OrderId}, updateId={IncomingUpdateId}, currentUpdateId={CurrentUpdateId}",
                    _config.SerialNumber,
                    order.OrderId,
                    order.OrderUpdateId,
                    _currentOrder.OrderUpdateId);
                return;
            }

            // Guard against delayed ORDER UPDATE from a previous orderId after we've switched to a new order.
            if (_currentOrder.OrderId != order.OrderId && order.OrderUpdateId > 0)
            {
                _logger.LogWarning(
                    "AGV {SerialNumber} ignored stale foreign order update: incomingOrderId={IncomingOrderId}, incomingUpdateId={IncomingUpdateId}, activeOrderId={ActiveOrderId}",
                    _config.SerialNumber,
                    order.OrderId,
                    order.OrderUpdateId,
                    _currentOrder.OrderId);
                return;
            }
        }

        // Check if this is an order update (same orderId, higher updateId)
        bool isOrderUpdate = _currentOrder != null 
            && _currentOrder.OrderId == order.OrderId 
            && order.OrderUpdateId > _currentOrder.OrderUpdateId;
        
        if (isOrderUpdate)
        {
            _logger.LogInformation("AGV {SerialNumber} received ORDER UPDATE {OrderId} (updateId: {OldUpdateId} -> {NewUpdateId})",
                _config.SerialNumber, order.OrderId, _currentOrder!.OrderUpdateId, order.OrderUpdateId);
            
            // Merge released nodes from the update
            // Find nodes in the new order that are now released
            int releasedCount = 0;
            foreach (var newNode in order.Nodes)
            {
                var existingNode = _currentOrder.Nodes.FirstOrDefault(n => n.NodeId == newNode.NodeId);
                if (existingNode != null)
                {
                    if (!existingNode.Released && newNode.Released)
                    {
                        existingNode.Released = true;
                        releasedCount++;
                        _logger.LogInformation("AGV {SerialNumber} - Node {NodeId} is now RELEASED",
                            _config.SerialNumber, newNode.NodeId);
                    }
                }
                else
                {
                    _currentOrder.Nodes.Add(newNode);
                    _logger.LogInformation("AGV {SerialNumber} - Node {NodeId} APPENDED from update",
                        _config.SerialNumber, newNode.NodeId);
                }
            }

            foreach (var newEdge in order.Edges)
            {
                if (!_currentOrder.Edges.Any(e => e.EdgeId == newEdge.EdgeId))
                {
                    _currentOrder.Edges.Add(newEdge);
                    _logger.LogInformation("AGV {SerialNumber} - Edge {EdgeId} APPENDED from update",
                        _config.SerialNumber, newEdge.EdgeId);
                }
            }
            
            // Update the order metadata
            _currentOrder.OrderUpdateId = order.OrderUpdateId;
            _currentState.OrderUpdateId = order.OrderUpdateId;
            
            // Resume movement if possible after ORDER UPDATE
            bool shouldResume = false;
            string resumeReason = "";

            if (_currentState.NewBaseRequest && releasedCount > 0)
            {
                // Case 1: AGV was explicitly waiting for released nodes (hit unreleased node)
                shouldResume = true;
                resumeReason = $"{releasedCount} previously unreleased nodes now released";
            }
            else if (!_isMoving && _currentNodeIndex >= 0 && _currentNodeIndex < _currentOrder.Nodes.Count)
            {
                // Case 2: AGV is idle but there are processable nodes ahead
                // This handles the scenario where the order "completed" (ran out of nodes)
                // but an ORDER UPDATE appended new nodes that can now be executed
                var nextNode = _currentOrder.Nodes[_currentNodeIndex];
                if (nextNode.Released)
                {
                    shouldResume = true;
                    // Back up index by 1 because ExecuteNextNodeAsync() increments it
                    _currentNodeIndex--;
                    resumeReason = $"new released nodes available at index {_currentNodeIndex + 1} after order update";
                }
            }

            if (shouldResume)
            {
                _logger.LogInformation("AGV {SerialNumber} resuming movement - {Reason}",
                    _config.SerialNumber, resumeReason);
                _currentState.NewBaseRequest = false;
                await PublishStateAsync(true);
                await ExecuteNextNodeAsync();
            }
            else
            {
                await PublishStateAsync(true);
            }
            
            return;
        }
        
        // New order - reset and start fresh.
        _actionCts?.Cancel();
        _actionCts = null;
        _currentOrder = order;
        _currentNodeIndex = -1;
        _isMoving = false;
        _isRotating = false;
        _isReversing = false;
        _arrivalTheta = null;
        _targetTheta = _currentTheta;
        _currentMovementBlocker = null;

        _currentState.OrderId = order.OrderId;
        _currentState.OrderUpdateId = order.OrderUpdateId;
        _currentState.NewBaseRequest = false;
        _currentState.ActionStates.Clear();
        _currentState.EdgeStates.Clear();
        
        // Log released/unreleased nodes
        var releasedNodes = order.Nodes.Where(n => n.Released).Select(n => n.NodeId).ToList();
        var unreleasedNodes = order.Nodes.Where(n => !n.Released).Select(n => n.NodeId).ToList();
        
        _logger.LogInformation("AGV {SerialNumber} processing NEW order {OrderId} - Nodes: {TotalCount} (released: [{Released}], unreleased: [{Unreleased}])",
            _config.SerialNumber, order.OrderId, order.Nodes.Count,
            string.Join(", ", releasedNodes),
            string.Join(", ", unreleasedNodes));

        // Publish state immediately on order received (state change)
        await PublishStateAsync(true);

        // Start executing order
        await ExecuteNextNodeAsync();
    }

    private async Task ExecuteNextNodeAsync()
    {
        if (_currentOrder == null || _currentOrder.Nodes.Count == 0)
            return;

        _currentNodeIndex++;

        if (_currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            _logger.LogInformation("AGV {SerialNumber} completed order {OrderId}",
                _config.SerialNumber, _currentOrder.OrderId);

            // Stop movement
            _currentState.Driving = false;
            _isMoving = false;
            _isRotating = false;
            _isReversing = false;
            _arrivalTheta = null;
            _targetTheta = _currentTheta;

            // Stop visualization timer
            StopVisualizationTimer();

            // Publish state immediately on order completion (state change)
            await PublishStateAsync(true);
            return;
        }

        var node = _currentOrder.Nodes[_currentNodeIndex];
        
        // VDA5050 Horizon Control: Check if node is released
        if (!node.Released)
        {
            _logger.LogWarning("AGV {SerialNumber} waiting at node index {Index} - next node {NodeId} is NOT RELEASED (traffic control)",
                _config.SerialNumber, _currentNodeIndex - 1, node.NodeId);
            
            // Request new base (signal that AGV needs more released nodes)
            _currentState.NewBaseRequest = true;
            
            // Stay at current position, stop movement
            _isMoving = false;
            _currentState.Driving = false;
            
            // Stop visualization timer while waiting
            StopVisualizationTimer();
            
            // Publish state to notify server we're waiting
            await PublishStateAsync(true);
            
            // Decrement index to retry this node when order is updated
            _currentNodeIndex--;
            return;
        }
        
        // Reset NewBaseRequest if we can proceed
        _currentState.NewBaseRequest = false;
        
        _logger.LogInformation("AGV {SerialNumber} executing node {NodeId} (sequence {Sequence}, released: {Released})",
            _config.SerialNumber, node.NodeId, node.SequenceId, node.Released);

        // Execute actions first
        if (node.Actions != null && node.Actions.Count > 0)
        {
            foreach (var action in node.Actions)
            {
                await ExecuteActionAsync(action);
            }
        }

        // Then move to node position
        if (node.NodePosition != null)
        {
            // Track previous node id for edge-aware speed lookup
            _prevNodeId = _currentNodeIndex > 0 && _currentOrder.Nodes.Count > 0
                ? _currentOrder.Nodes[_currentNodeIndex - 1].NodeId
                : string.Empty;
            var orderEdge = FindOrderEdge(_prevNodeId, node.NodeId);

            _targetX = node.NodePosition.X;
            _targetY = node.NodePosition.Y;
            ConfigureMovementPlan(node, orderEdge);

            // ── Cross-Map: propagate MapId to state and visualization ──
            var previousMapId = _currentMapId;
            _currentMapId = node.NodePosition.MapId;
            if (_currentState.AgvPosition != null)
                _currentState.AgvPosition.MapId = _currentMapId;
            if (_currentVisualization?.AgvPosition != null)
                _currentVisualization.AgvPosition.MapId = _currentMapId;
            if (!string.IsNullOrEmpty(previousMapId) && previousMapId != _currentMapId)
            {
                _logger.LogInformation(
                    "AGV {Serial}: Map transition {PrevMap} → {NewMap} at node {NodeId}",
                    _config.SerialNumber, previousMapId, _currentMapId, node.NodeId);
            }

            _isMoving = true;
            _currentState.Driving = true;

            // Cache the current edge maxSpeed
            _currentEdgeMaxSpeed = orderEdge?.MaxSpeed ?? GetCurrentEdgeMaxSpeed();

            // Active edge is dynamically tracked by SyncOrderStates()

            // Start visualization timer when movement begins
            StartVisualizationTimer();

            // Publish state immediately when movement starts (state change)
            await PublishStateAsync(true);

            _logger.LogInformation(
                "AGV {SerialNumber} moving to ({X}, {Y}) with heading {Heading:F2} rad ({Direction})",
                _config.SerialNumber,
                _targetX,
                _targetY,
                _targetTheta,
                _isReversing ? "reverse" : "forward");
        }
        else
        {
            // No position, move to next node
            await ExecuteNextNodeAsync();
        }
    }

    private Edge? FindOrderEdge(string fromNodeId, string toNodeId)
    {
        if (_currentOrder == null || string.IsNullOrEmpty(fromNodeId))
            return null;

        return _currentOrder.Edges.FirstOrDefault(edge =>
            edge.StartNodeId.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase) &&
            edge.EndNodeId.Equals(toNodeId, StringComparison.OrdinalIgnoreCase));
    }

    private void ConfigureMovementPlan(Node node, Edge? orderEdge)
    {
        var dx = _targetX - _currentX;
        var dy = _targetY - _currentY;
        var travelTheta = NormalizeAngle(Math.Atan2(dy, dx));
        var nodeTheta = node.NodePosition?.Theta.HasValue == true
            ? NormalizeAngle(node.NodePosition!.Theta!.Value)
            : (double?)null;
        var edgeTheta = orderEdge?.Orientation.HasValue == true
            ? NormalizeAngle(orderEdge.Orientation!.Value)
            : (double?)null;
        var edgeOrientationType = orderEdge?.OrientationType?.Trim();

        _arrivalTheta = nodeTheta;
        if (_arrivalTheta == null &&
            edgeTheta.HasValue &&
            string.Equals(edgeOrientationType, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            _arrivalTheta = edgeTheta.Value;
        }

        if (edgeTheta.HasValue &&
            string.Equals(edgeOrientationType, "GLOBAL", StringComparison.OrdinalIgnoreCase) &&
            TryResolveHeadingPlan(edgeTheta.Value, travelTheta, out var reverseFromEdge))
        {
            _targetTheta = edgeTheta.Value;
            _isReversing = reverseFromEdge;
            return;
        }

        if (nodeTheta.HasValue && TryResolveHeadingPlan(nodeTheta.Value, travelTheta, out var reverseFromNode))
        {
            _targetTheta = nodeTheta.Value;
            _isReversing = reverseFromNode;
            return;
        }

        if (edgeTheta.HasValue && TryResolveHeadingPlan(edgeTheta.Value, travelTheta, out var reverseFromOrientation))
        {
            _targetTheta = edgeTheta.Value;
            _isReversing = reverseFromOrientation;
            return;
        }

        var forwardHeading = travelTheta;
        var reverseHeading = NormalizeAngle(travelTheta + Math.PI);
        var forwardRotation = Math.Abs(NormalizeAngle(forwardHeading - _currentTheta));
        var reverseRotation = Math.Abs(NormalizeAngle(reverseHeading - _currentTheta));

        if (reverseRotation + 0.05 < forwardRotation)
        {
            _targetTheta = reverseHeading;
            _isReversing = true;
            return;
        }

        _targetTheta = forwardHeading;
        _isReversing = false;
    }

    private bool TryResolveHeadingPlan(double desiredHeading, double travelTheta, out bool reverse)
    {
        var normalizedDesiredHeading = NormalizeAngle(desiredHeading);
        var reverseHeading = NormalizeAngle(travelTheta + Math.PI);

        if (Math.Abs(NormalizeAngle(normalizedDesiredHeading - travelTheta)) <= MovementHeadingAlignmentTolerance)
        {
            reverse = false;
            return true;
        }

        if (Math.Abs(NormalizeAngle(normalizedDesiredHeading - reverseHeading)) <= MovementHeadingAlignmentTolerance)
        {
            reverse = true;
            return true;
        }

        reverse = false;
        return false;
    }

    private void StartVisualizationTimer()
    {
        if (_visualizationTimer != null)
            return;

        // Publish visualization at 200ms while moving
        _visualizationTimer = new Timer(async _ => await PublishVisualizationAsync(), null,
            TimeSpan.FromMilliseconds(_movementConfig.VisualizationPublishInterval),
            TimeSpan.FromMilliseconds(_movementConfig.VisualizationPublishInterval));

        _logger.LogDebug("AGV {SerialNumber} started visualization timer ({Interval}ms)",
            _config.SerialNumber, _movementConfig.VisualizationPublishInterval);
    }

    private void StopVisualizationTimer()
    {
        if (_visualizationTimer != null)
        {
            _visualizationTimer.Dispose();
            _visualizationTimer = null;
            _logger.LogDebug("AGV {SerialNumber} stopped visualization timer", _config.SerialNumber);
        }
    }

    private async Task UpdateMovementAsync()
    {
        if (!_isMoving)
            return;

        // Safety check: validate _currentOrder and _currentNodeIndex before accessing
        if (_currentOrder == null || _currentOrder.Nodes == null ||
            _currentNodeIndex < 0 || _currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            _isMoving = false;
            _isRotating = false;
            _currentState.Driving = false;
            return;
        }

        // Calculate distance to target
        double dx = _targetX - _currentX;
        double dy = _targetY - _currentY;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        // Arrival check FIRST: when the AGV is already within tolerance of the target node,
        // snap the final orientation (if required) and proceed — do NOT attempt a rotation
        // phase that could be blocked by a nearby AGV (e.g. at the start node which is
        // co-located with the AGV's current position).
        if (distance < _movementConfig.Tolerance)
        {
            _currentX = _targetX;
            _currentY = _targetY;

            // Clear active edge on arrival
            // Update last node when arrived
            var currentNode = _currentOrder.Nodes[_currentNodeIndex];
            _currentState.LastNodeId = currentNode.NodeId;
            _currentState.LastNodeSequenceId = currentNode.SequenceId;

            // Update position
            _currentState.AgvPosition!.X = _currentX;
            _currentState.AgvPosition.Y = _currentY;
            _currentVisualization.AgvPosition!.X = _currentX;
            _currentVisualization.AgvPosition.Y = _currentY;

            if (_arrivalTheta.HasValue)
            {
                _currentTheta = NormalizeAngle(_arrivalTheta.Value);
            }

            _currentState.AgvPosition.Theta = ToServerTheta(_currentTheta);
            _currentVisualization.AgvPosition.Theta = ToServerTheta(_currentTheta);
            _currentState.Velocity!.Vx = 0;
            _currentState.Velocity.Vy = 0;
            _currentState.Velocity.Omega = 0;
            _currentVisualization.Velocity!.Vx = 0;
            _currentVisualization.Velocity.Vy = 0;
            _currentVisualization.Velocity.Omega = 0;
            _currentState.Driving = false;
            _isMoving = false;
            _isRotating = false;
            _isReversing = false;
            _arrivalTheta = null;
            _targetTheta = _currentTheta;

            // Stop visualization timer when movement stops
            StopVisualizationTimer();

            _logger.LogInformation("AGV {SerialNumber} arrived at node {NodeId} ({X}, {Y})",
                _config.SerialNumber, currentNode.NodeId, _currentX, _currentY);

            // Publish state immediately on arrival (state change)
            await PublishStateAsync(true);

            // Move to next node
            await ExecuteNextNodeAsync();
            return;
        }

        // Phase 1: align the body heading before translating (only when distance > tolerance).
        double headingTarget = _targetTheta;
        double angleDiff = NormalizeAngle(headingTarget - _currentTheta);
        double rotationTolerance = 0.05; // ~3 degrees tolerance

        if (Math.Abs(angleDiff) > rotationTolerance)
        {
            _isRotating = true;

            // Ensure linear velocity is zero during rotation to prevent sliding
            _currentState.Velocity!.Vx = 0;
            _currentState.Velocity.Vy = 0;
            _currentVisualization.Velocity!.Vx = 0;
            _currentVisualization.Velocity.Vy = 0;

            // Dynamic fleet safety: stop rotation when front area is blocked.
            if (IsMovementBlocked(_currentX, _currentY, true))
                return;

            _currentState.Driving = true;

            // Rotation speed (radians per update)
            // 0.05 rad/100ms = 0.5 rad/s ~= 28.6 degrees/s
            double rotationSpeed = 0.05;
            double rotationStep = Math.Sign(angleDiff) * Math.Min(Math.Abs(angleDiff), rotationSpeed);

            _currentTheta += rotationStep;
            _currentTheta = NormalizeAngle(_currentTheta);

            // Update state during rotation
            _currentState.AgvPosition!.Theta = ToServerTheta(_currentTheta);
            _currentState.Velocity!.Omega = rotationStep * 1000.0 / _movementConfig.MovementUpdateInterval; // rad/s

            // Update visualization
            _currentVisualization.AgvPosition!.Theta = ToServerTheta(_currentTheta);
            _currentVisualization.Velocity!.Omega = _currentState.Velocity.Omega;

            // Log rotation progress occasionally (every ~1 second)
            if (DateTime.UtcNow.Millisecond < 200)
            {
                _logger.LogDebug("AGV {SerialNumber} rotating... Current: {Current:F2}, Target: {Target:F2}, Diff: {Diff:F2}",
                    _config.SerialNumber, _currentTheta, headingTarget, angleDiff);
            }

            return;
        }

        // Phase 2: translate toward the target while keeping the chosen heading.
        _isRotating = false;
        _currentState.Velocity!.Omega = 0;
        _currentVisualization.Velocity!.Omega = 0;

        // Move towards target
        double edgeMaxSpeed = _currentEdgeMaxSpeed > 0 ? _currentEdgeMaxSpeed : _movementConfig.Speed;
        double fastSpeed = edgeMaxSpeed;
        double stepDistance = fastSpeed * _movementConfig.MovementUpdateInterval / 1000.0; // m
        double ratio = Math.Min(stepDistance / distance, 1.0);

        var nextX = _currentX + dx * ratio;
        var nextY = _currentY + dy * ratio;

        // Dynamic fleet safety: no coordinate overlap and no moving into blocked front area.
        if (IsMovementBlocked(nextX, nextY, false))
            return;

        _currentState.Driving = true;
        _currentX = nextX;
        _currentY = nextY;

        // Battery drain when moving
        if (_batteryConfig.Enabled && !_isCharging)
        {
            double actualMoved = distance * ratio;
            _batteryLevel = Math.Max(0, _batteryLevel - actualMoved * _batteryConfig.DrainRatePerMeter);
            _currentState.BatteryState.BatteryCharge = _batteryLevel;
            await CheckBatteryWarningAsync();
        }

        // Update state
        _currentState.AgvPosition!.X = _currentX;
        _currentState.AgvPosition.Y = _currentY;
        _currentState.AgvPosition.Theta = ToServerTheta(_currentTheta);
        _currentState.Velocity!.Vx = (dx / distance) * fastSpeed;
        _currentState.Velocity.Vy = (dy / distance) * fastSpeed;
        _currentState.DistanceSinceLastNode += stepDistance;

        // Update visualization
        _currentVisualization.AgvPosition!.X = _currentX;
        _currentVisualization.AgvPosition.Y = _currentY;
        _currentVisualization.AgvPosition.Theta = ToServerTheta(_currentTheta);
        _currentVisualization.Velocity!.Vx = _currentState.Velocity.Vx;
        _currentVisualization.Velocity.Vy = _currentState.Velocity.Vy;
    }

    private bool IsMovementBlocked(double nextX, double nextY, bool isRotating)
    {
        if (_movementBlockResolver == null)
            return false;

        var blockingAgv = _movementBlockResolver(
            _config.SerialNumber,
            _currentX,
            _currentY,
            nextX,
            nextY,
            _currentTheta,
            isRotating,
            _currentMapId);

        if (string.IsNullOrEmpty(blockingAgv))
        {
            if (!string.IsNullOrEmpty(_currentMovementBlocker))
            {
                _logger.LogInformation("AGV {SerialNumber} path clear, resuming movement", _config.SerialNumber);
                _currentMovementBlocker = null;
            }
            return false;
        }

        _currentState.Driving = false;
        _currentState.Velocity!.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;
        _currentVisualization.Velocity!.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;

        var now = DateTime.UtcNow;
        if (_currentMovementBlocker != blockingAgv || now - _lastBlockLogAt > TimeSpan.FromSeconds(2))
        {
            _logger.LogWarning("AGV {SerialNumber} movement blocked by AGV {BlockingAgv}",
                _config.SerialNumber, blockingAgv);
            _lastBlockLogAt = now;
        }

        _currentMovementBlocker = blockingAgv;
        return true;
    }

    // Helper method to normalize angle to [-pi, pi].
    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI)
        {
            angle -= 2 * Math.PI;
        }

        while (angle < -Math.PI)
        {
            angle += 2 * Math.PI;
        }

        return angle;
    }

    // VDA5050 expects theta in radians within [-pi, pi].
    private double ToServerTheta(double radians)
    {
        return NormalizeAngle(radians);
    }

    private async Task ExecuteActionAsync(VdaAction action)
    {
        _logger.LogInformation("AGV {SerialNumber} executing action {ActionType} (id: {ActionId})",
            _config.SerialNumber, action.ActionType, action.ActionId);

        var actionState = new ActionState
        {
            ActionId = action.ActionId,
            ActionType = action.ActionType,
            ActionStatus = "WAITING",
            ActionDescription = action.ActionDescription
        };

        _currentState.ActionStates.Add(actionState);

        // Publish state when action starts (state change)
        await PublishStateAsync(true);

        try
        {
            actionState.ActionStatus = "RUNNING";
            await PublishStateAsync(true);

            switch (action.ActionType.ToUpper())
            {
                case "PICK":
                case "LIFT":
                    await Task.Delay(_actionsConfig.LiftDurationMs);
                    _hasLoad = true;
                    _currentState.Loads.Add(new Load
                    {
                        LoadId = $"LOAD_{DateTime.UtcNow.Ticks}",
                        LoadType = "PALLET",
                        Weight = 100.0
                    });
                    _logger.LogInformation("AGV {SerialNumber} picked up load", _config.SerialNumber);
                    break;

                case "DROP":
                case "LOWER":
                    await Task.Delay(_actionsConfig.LowerDurationMs);
                    _hasLoad = false;
                    _currentState.Loads.Clear();
                    _logger.LogInformation("AGV {SerialNumber} dropped load", _config.SerialNumber);
                    break;

                case "WAIT":
                    var durationParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "duration");
                    int waitMs = durationParam != null ? Convert.ToInt32(durationParam.Value) : 1000;
                    await Task.Delay(waitMs);
                    _logger.LogInformation("AGV {SerialNumber} waited {Duration}ms", _config.SerialNumber, waitMs);
                    break;

                default:
                    _logger.LogWarning("AGV {SerialNumber} unknown action type {ActionType}",
                        _config.SerialNumber, action.ActionType);
                    break;
            }

            actionState.ActionStatus = "FINISHED";
            _logger.LogInformation("AGV {SerialNumber} finished action {ActionType}", _config.SerialNumber, action.ActionType);
        }
        catch (Exception ex)
        {
            actionState.ActionStatus = "FAILED";
            _logger.LogError(ex, "AGV {SerialNumber} failed action {ActionType}", _config.SerialNumber, action.ActionType);
        }

        // Publish state when action completes (state change)
        await PublishStateAsync(true);
    }

    private void SyncOrderStates()
    {
        if (_currentOrder == null) return;
        _currentState.NodeStates.Clear();
        int startIndex = Math.Max(0, _currentNodeIndex);
        if (startIndex < _currentOrder.Nodes.Count)
        {
            for (int j = startIndex; j < _currentOrder.Nodes.Count; j++)
            {
                var node = _currentOrder.Nodes[j];
                _currentState.NodeStates.Add(new NodeState
                {
                    NodeId = node.NodeId,
                    SequenceId = node.SequenceId,
                    Released = node.Released,
                    NodePosition = node.NodePosition
                });
            }
        }
        _currentState.EdgeStates.Clear();
        if (_isMoving && _currentNodeIndex > 0 && _currentNodeIndex < _currentOrder.Nodes.Count)
        {
            var targetNode = _currentOrder.Nodes[_currentNodeIndex];
            var prevNode = _currentOrder.Nodes[_currentNodeIndex - 1];
            var orderEdge = FindOrderEdge(prevNode.NodeId, targetNode.NodeId);
            var activeEdge = orderEdge != null ? new SimEdgeDto
            {
                EdgeId = orderEdge.EdgeId,
                StartNodeId = orderEdge.StartNodeId,
                EndNodeId = orderEdge.EndNodeId,
                MaxSpeed = orderEdge.MaxSpeed,
                Length = orderEdge.Length
            } : FindEdgeAcrossMaps(prevNode.NodeId, targetNode.NodeId);
            if (activeEdge != null)
            {
                _currentState.EdgeStates.Add(new EdgeState
                {
                    EdId = activeEdge.EdgeId,
                    SequenceId = targetNode.SequenceId,
                    Released = targetNode.Released
                });
            }
        }
    }

    private async Task PublishStateAsync(bool immediate)
    {
        try
        {
            SyncOrderStates();
            // Battery simulation (idle drain + charging) on periodic ticks
            if (!immediate && _batteryConfig.Enabled)
            {
                var now = DateTime.UtcNow;
                double elapsedMinutes = (now - _lastIdleDrainTime).TotalMinutes;
                _lastIdleDrainTime = now;

                if (_isCharging)
                {
                    _batteryLevel = Math.Min(100.0, _batteryLevel + _batteryConfig.ChargeRatePerMinute * elapsedMinutes);
                    _currentState.BatteryState.BatteryCharge = _batteryLevel;
                    _logger.LogDebug("AGV {SerialNumber} charging: {Level:F1}%", _config.SerialNumber, _batteryLevel);
                }
                else if (!_isMoving)
                {
                    // Idle drain
                    _batteryLevel = Math.Max(0, _batteryLevel - _batteryConfig.DrainRateIdlePerMinute * elapsedMinutes);
                    _currentState.BatteryState.BatteryCharge = _batteryLevel;
                }

                await CheckBatteryWarningAsync();
            }

            // Network chaos: Packet loss
            if (_chaosPacketLossPercent > 0 && _chaosRandom.Next(100) < _chaosPacketLossPercent)
            {
                _logger.LogDebug("AGV {SerialNumber} state publish DROPPED (packet loss {Pct}%)",
                    _config.SerialNumber, _chaosPacketLossPercent);
                return;
            }

            // Network chaos: Latency
            if (_chaosMaxLatencyMs > 0)
            {
                int delay = _chaosMinLatencyMs == _chaosMaxLatencyMs
                    ? _chaosMinLatencyMs
                    : _chaosRandom.Next(_chaosMinLatencyMs, _chaosMaxLatencyMs);
                if (delay > 0)
                    await Task.Delay(delay);
            }

            _currentState.HeaderId = _headerIdCounter++;
            _currentState.Timestamp = DateTime.UtcNow.ToString("o");

            var json = JsonSerializer.Serialize(_currentState);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(StateTopic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce)
                .Build();

            await _mqttClient.PublishAsync(message);

            if (immediate)
            {
                _logger.LogInformation("AGV {SerialNumber} published state (immediate) - Driving: {Driving}, Position: ({X:F2}, {Y:F2}), MapId: '{MapId}'",
                    _config.SerialNumber, _currentState.Driving, _currentX, _currentY, _currentState.AgvPosition?.MapId ?? "");
            }
            else
            {
                _logger.LogInformation("AGV {SerialNumber} published state (periodic) - Position: ({X:F2}, {Y:F2}), MapId: '{MapId}', Battery: {Battery:F1}%",
                    _config.SerialNumber, _currentX, _currentY, _currentState.AgvPosition?.MapId ?? "", _batteryLevel);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish state for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    private async Task CheckBatteryWarningAsync()
    {
        if (!_batteryConfig.Enabled) return;

        bool shouldWarn = _batteryLevel < _batteryConfig.CriticalLevel;

        if (shouldWarn && !_batteryWarningActive)
        {
            _batteryWarningActive = true;
            var existing = _currentState.Errors.FirstOrDefault(e => e.ErrorType == "BATTERY_CRITICAL");
            if (existing == null)
            {
                _currentState.Errors.Add(new VdaError
                {
                    ErrorType = "BATTERY_CRITICAL",
                    ErrorLevel = "WARNING",
                    ErrorDescription = $"Battery level critical: {_batteryLevel:F1}%"
                });
                _logger.LogWarning("AGV {SerialNumber} BATTERY CRITICAL: {Level:F1}%", _config.SerialNumber, _batteryLevel);
            }
        }
        else if (!shouldWarn && _batteryWarningActive)
        {
            _batteryWarningActive = false;
            _currentState.Errors.RemoveAll(e => e.ErrorType == "BATTERY_CRITICAL");
        }

        await Task.CompletedTask;
    }

    private async Task PublishVisualizationAsync()
    {
        try
        {
            _currentVisualization.HeaderId = _headerIdCounter++;
            _currentVisualization.Timestamp = DateTime.UtcNow;

            var json = JsonSerializer.Serialize(_currentVisualization);
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(VisualizationTopic)
                .WithPayload(json)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message);

            _logger.LogDebug("AGV {SerialNumber} published visualization - Position: ({X:F2}, {Y:F2}), Velocity: ({Vx:F2}, {Vy:F2})",
                _config.SerialNumber,
                _currentVisualization.AgvPosition?.X ?? 0,
                _currentVisualization.AgvPosition?.Y ?? 0,
                _currentVisualization.Velocity?.Vx ?? 0,
                _currentVisualization.Velocity?.Vy ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish visualization for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    public async Task StopAsync()
    {
        _isRunning = false;

        _statePublishTimer?.Dispose();
        _movementTimer?.Dispose();
        StopVisualizationTimer();

        _logger.LogInformation("AGV {SerialNumber} stopped", _config.SerialNumber);
        await Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (_mqttClient.IsConnected)
        {
            // Publish OFFLINE gracefully before disconnecting
            await PublishConnectionStateAsync(VDA5050ConnectionState.Offline);
            await _mqttClient.DisconnectAsync();
            _logger.LogInformation("AGV {SerialNumber} disconnected from MQTT broker", _config.SerialNumber);
        }
    }

    private async Task PublishConnectionStateAsync(string state)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new Vda5050Connection
            {
                HeaderId = _headerIdCounter++,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Version = "2.0.0",
                Manufacturer = _config.Manufacturer,
                SerialNumber = _config.SerialNumber,
                ConnectionState = state
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(ConnectionTopic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithRetainFlag(true)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message);
            _logger.LogInformation("AGV {SerialNumber} published connection state: {State}", _config.SerialNumber, state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish connection state for AGV {SerialNumber}", _config.SerialNumber);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _statePublishTimer?.Dispose();
        _movementTimer?.Dispose();
        _manualPositionTimer?.Dispose();
        StopVisualizationTimer();
        _actionCts?.Dispose();
        _mqttClient?.Dispose();

        _disposed = true;
    }

    public void SetPosition(double x, double y, double theta, string mapId)
    {
        var previousX = _currentX;
        var previousY = _currentY;
        var previousTheta = _currentTheta;
        var now = DateTime.UtcNow;

        _currentX = x;
        _currentY = y;
        _currentTheta = theta;
        _targetTheta = theta;
        _arrivalTheta = null;
        _isReversing = false;
        _currentMapId = mapId;

        _currentState.AgvPosition!.X = x;
        _currentState.AgvPosition.Y = y;
        _currentState.AgvPosition.Theta = ToServerTheta(theta);
        _currentState.AgvPosition.MapId = mapId;
        _currentState.LastNodeId = "0";
        _currentVisualization.AgvPosition!.X = x;
        _currentVisualization.AgvPosition.Y = y;
        _currentVisualization.AgvPosition.Theta = ToServerTheta(theta);
        _currentVisualization.AgvPosition.MapId = mapId;
        UpdateManualVelocity(previousX, previousY, previousTheta, now);

        // Start visualization publishing for manual position updates (joystick)
        _lastManualPositionUpdate = now;
        StartManualPositionPublishing();

        _logger.LogInformation("AGV {SerialNumber} position updated to ({X}, {Y}, {Theta}) on map {MapId}",
            _config.SerialNumber, x, y, theta, mapId);
    }

    /// <summary>
    /// Start visualization timer for manual position control (joystick).
    /// Auto-stops after ManualPositionTimeout of inactivity.
    /// </summary>
    private void StartManualPositionPublishing()
    {
        if (_manualPositionTimer != null)
            return;

        // Start visualization timer to publish position via MQTT
        StartVisualizationTimer();

        // Start a watchdog timer that checks for inactivity and stops publishing
        _manualPositionTimer = new Timer(_ =>
        {
            if ((DateTime.UtcNow - _lastManualPositionUpdate) > ManualPositionTimeout)
            {
                StopManualPositionPublishing();
            }
        }, null, ManualPositionTimeout, TimeSpan.FromMilliseconds(200));

        _logger.LogDebug("AGV {SerialNumber} started manual position publishing", _config.SerialNumber);
    }

    private void StopManualPositionPublishing()
    {
        if (_manualPositionTimer != null)
        {
            _manualPositionTimer.Dispose();
            _manualPositionTimer = null;

            // Only stop visualization timer if AGV is not order-moving
            if (!_isMoving)
            {
                _currentState.Driving = false;
                _currentState.Velocity!.Vx = 0;
                _currentState.Velocity.Vy = 0;
                _currentState.Velocity.Omega = 0;
                _currentVisualization.Velocity!.Vx = 0;
                _currentVisualization.Velocity.Vy = 0;
                _currentVisualization.Velocity.Omega = 0;
                _lastManualPoseAt = DateTime.MinValue;
                StopVisualizationTimer();
            }

            _logger.LogDebug("AGV {SerialNumber} stopped manual position publishing", _config.SerialNumber);
        }
    }

    private void UpdateManualVelocity(double previousX, double previousY, double previousTheta, DateTime now)
    {
        double velocityX = 0;
        double velocityY = 0;
        double omega = 0;

        if (_lastManualPoseAt != DateTime.MinValue)
        {
            var dt = (now - _lastManualPoseAt).TotalSeconds;
            if (dt > 1e-6)
            {
                velocityX = (_currentX - previousX) / dt;
                velocityY = (_currentY - previousY) / dt;
                omega = NormalizeAngle(_currentTheta - previousTheta) / dt;
            }
        }

        _lastManualPoseAt = now;
        _currentState.Velocity!.Vx = velocityX;
        _currentState.Velocity.Vy = velocityY;
        _currentState.Velocity.Omega = omega;
        _currentVisualization.Velocity!.Vx = velocityX;
        _currentVisualization.Velocity.Vy = velocityY;
        _currentVisualization.Velocity.Omega = omega;

        if (!_isMoving)
        {
            _currentState.Driving =
                Math.Abs(velocityX) > ManualVelocityEpsilon ||
                Math.Abs(velocityY) > ManualVelocityEpsilon ||
                Math.Abs(omega) > ManualVelocityEpsilon;
        }
    }

    public void SetSpeed(double speed)
    {
        _movementConfig.Speed = speed;
        _logger.LogInformation("AGV {SerialNumber} speed updated to {Speed} m/s", _config.SerialNumber, speed);
    }

    public void SetBatteryLevel(double level)
    {
        _batteryLevel = Math.Clamp(level, 0, 100);
        _currentState.BatteryState.BatteryCharge = _batteryLevel;
        _logger.LogInformation("AGV {SerialNumber} battery manually set to {Level:F1}%", _config.SerialNumber, _batteryLevel);
    }

    public async Task LiftAsync()
    {
        await Task.Delay(_actionsConfig.LiftDurationMs);
        _hasLoad = true;
        _currentState.Loads.Add(new Load
        {
            LoadId = $"LOAD_{DateTime.UtcNow.Ticks}",
            LoadType = "PALLET",
            Weight = 100.0
        });
        _logger.LogInformation("AGV {SerialNumber} lift (manual control)", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    public async Task LowerAsync()
    {
        await Task.Delay(_actionsConfig.LowerDurationMs);
        _hasLoad = false;
        _currentState.Loads.Clear();
        _logger.LogInformation("AGV {SerialNumber} lower (manual control)", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    // ── Network Chaos ──────────────────────────────────────────

    /// <summary>Simulate MQTT latency. Pass (0,0) to disable.</summary>
    public void SetChaosLatency(int minMs, int maxMs)
    {
        _chaosMinLatencyMs = Math.Max(0, minMs);
        _chaosMaxLatencyMs = Math.Max(0, maxMs);
        _logger.LogInformation("AGV {SerialNumber} chaos latency set to {Min}-{Max}ms", _config.SerialNumber, minMs, maxMs);
    }

    /// <summary>Simulate packet loss. 0 = off, 100 = drop all state publishes.</summary>
    public void SetPacketLoss(int percent)
    {
        _chaosPacketLossPercent = Math.Clamp(percent, 0, 100);
        _logger.LogInformation("AGV {SerialNumber} chaos packet loss set to {Pct}%", _config.SerialNumber, percent);
    }

    /// <summary>Force-disconnect from MQTT broker, automatically reconnect after durationMs.</summary>
    public async Task TriggerDisconnectAsync(int durationMs)
    {
        if (!_mqttClient.IsConnected) return;

        _logger.LogWarning("AGV {SerialNumber} chaos disconnect triggered ({Duration}ms)", _config.SerialNumber, durationMs);

        await _mqttClient.DisconnectAsync();

        _ = Task.Run(async () =>
        {
            await Task.Delay(durationMs);
            try
            {
                await ConnectAsync();
                _logger.LogInformation("AGV {SerialNumber} chaos reconnected after {Duration}ms", _config.SerialNumber, durationMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AGV {SerialNumber} failed to reconnect after chaos disconnect", _config.SerialNumber);
            }
        });
    }

    /// <summary>
    /// Add an error to the AGV state. Error will be sent with the next state publish.
    /// </summary>
    /// <param name="errorType">Type of error (e.g., "HARDWARE", "NAVIGATION", "COMMUNICATION")</param>
    /// <param name="errorLevel">Level of error: "WARNING", "FATAL"</param>
    /// <param name="description">Optional description of the error</param>
    public async Task AddErrorAsync(string errorType, string errorLevel = "WARNING", string? description = null)
    {
        var error = new VdaError
        {
            ErrorType = errorType,
            ErrorLevel = errorLevel,
            ErrorDescription = description
        };

        _currentState.Errors.Add(error);

        _logger.LogWarning("AGV {SerialNumber} added error: Type={ErrorType}, Level={ErrorLevel}, Description={Description}",
            _config.SerialNumber, errorType, errorLevel, description ?? "N/A");

        // Publish state immediately when error changes
        await PublishStateAsync(true);
    }

    /// <summary>
    /// Clear a specific error from the AGV state by error type.
    /// </summary>
    /// <param name="errorType">Type of error to clear</param>
    /// <returns>True if error was found and removed, false otherwise</returns>
    public async Task<bool> ClearErrorAsync(string errorType)
    {
        var errorToRemove = _currentState.Errors.FirstOrDefault(e => 
            e.ErrorType.Equals(errorType, StringComparison.OrdinalIgnoreCase));

        if (errorToRemove != null)
        {
            _currentState.Errors.Remove(errorToRemove);

            _logger.LogInformation("AGV {SerialNumber} cleared error: Type={ErrorType}",
                _config.SerialNumber, errorType);

            // Publish state immediately when error changes
            await PublishStateAsync(true);
            return true;
        }

        _logger.LogWarning("AGV {SerialNumber} tried to clear non-existent error: Type={ErrorType}",
            _config.SerialNumber, errorType);
        return false;
    }

    /// <summary>
    /// Clear all errors from the AGV state.
    /// </summary>
    /// <returns>Number of errors cleared</returns>
    public async Task<int> ClearAllErrorsAsync()
    {
        int count = _currentState.Errors.Count;

        if (count > 0)
        {
            _currentState.Errors.Clear();

            _logger.LogInformation("AGV {SerialNumber} cleared all errors ({Count} errors removed)",
                _config.SerialNumber, count);

            // Publish state immediately when errors change
            await PublishStateAsync(true);
        }

        return count;
    }

    /// <summary>
    /// Inject a predefined error template with its side-effect applied.
    /// </summary>
    public async Task InjectErrorTemplateAsync(ErrorTemplate template)
    {
        _logger.LogWarning("AGV {SerialNumber} injecting error template: {Name} (level: {Level})",
            _config.SerialNumber, template.Name, template.ErrorLevel);

        // Remove existing same-type error first
        _currentState.Errors.RemoveAll(e => e.ErrorType == template.ErrorType);

        _currentState.Errors.Add(new VdaError
        {
            ErrorType = template.ErrorType,
            ErrorLevel = template.ErrorLevel,
            ErrorDescription = template.Description
        });

        // Apply side-effects
        switch (template.SideEffect)
        {
            case ErrorSideEffect.ReduceSpeed50Percent:
                _movementConfig.Speed *= 0.5;
                _logger.LogWarning("AGV {SerialNumber} speed reduced to {Speed} m/s", _config.SerialNumber, _movementConfig.Speed);
                break;

            case ErrorSideEffect.StopMovement:
                _isMoving = false;
                _currentState.Driving = false;
                StopVisualizationTimer();
                _logger.LogWarning("AGV {SerialNumber} movement stopped due to error", _config.SerialNumber);
                break;

            case ErrorSideEffect.EmergencyStop:
                _isMoving = false;
                _isRotating = false;
                _currentState.Driving = false;
                _currentState.Paused = true;
                _currentState.SafetyState.EStop = "MANUAL";
                _currentState.SafetyState.FieldViolation = true;
                StopVisualizationTimer();
                _actionCts?.Cancel();
                _logger.LogCritical("AGV {SerialNumber} EMERGENCY STOP activated", _config.SerialNumber);
                break;

            case ErrorSideEffect.LocalizationLost:
                _currentState.AgvPosition!.PositionInitialized = false;
                _currentState.AgvPosition.LocalizationScore = 0.0;
                _isMoving = false;
                _currentState.Driving = false;
                StopVisualizationTimer();
                _logger.LogCritical("AGV {SerialNumber} localization LOST", _config.SerialNumber);
                break;

            case ErrorSideEffect.ClearLoads:
                _hasLoad = false;
                _currentState.Loads.Clear();
                _isMoving = false;
                _currentState.Driving = false;
                StopVisualizationTimer();
                _logger.LogCritical("AGV {SerialNumber} loads CLEARED due to drop", _config.SerialNumber);
                break;
        }

        await PublishStateAsync(true);
    }

    /// <summary>
    /// Clear the side-effects of an EMERGENCY_STOP error template.
    /// </summary>
    public async Task ClearEmergencyStopAsync()
    {
        _currentState.SafetyState.EStop = "NONE";
        _currentState.SafetyState.FieldViolation = false;
        _currentState.Paused = false;
        _currentState.Errors.RemoveAll(e => e.ErrorType == "safety");
        _logger.LogInformation("AGV {SerialNumber} emergency stop cleared", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    /// <summary>
    /// Restore localization after LOCALIZATION_LOST error.
    /// </summary>
    public async Task RestoreLocalizationAsync()
    {
        _currentState.AgvPosition!.PositionInitialized = true;
        _currentState.AgvPosition.LocalizationScore = 1.0;
        _currentState.Errors.RemoveAll(e => e.ErrorType == "localization");
        _logger.LogInformation("AGV {SerialNumber} localization restored", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    /// <summary>
    /// Get current errors list
    /// </summary>
    public IReadOnlyList<VdaError> GetErrors() => _currentState.Errors.AsReadOnly();

    /// <summary>
    /// Check if AGV has any errors
    /// </summary>
    public bool HasErrors => _currentState.Errors.Count > 0;

    /// <summary>
    /// Check if AGV has any fatal errors
    /// </summary>
    public bool HasFatalErrors => _currentState.Errors.Any(e => 
        e.ErrorLevel.Equals("FATAL", StringComparison.OrdinalIgnoreCase));
}
