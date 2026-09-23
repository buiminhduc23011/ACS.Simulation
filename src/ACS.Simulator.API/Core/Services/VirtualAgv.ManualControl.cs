using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — Manual control: joystick position updates, map graph management,
/// movement block resolver, and public command APIs.
/// </summary>
public partial class VirtualAgv
{
    // ── Map Graph Management ─────────────────────────────────────────────

    /// <inheritdoc/>
    public void SetMapGraph(SimMapGraph? graph)
    {
        _mapGraph = graph;
        if (graph != null && !string.IsNullOrEmpty(graph.MapId))
        {
            _mapGraphs[graph.MapId] = graph;
        }
        TryPopulateLastNodeIdFromCurrentPose();
        _logger.LogInformation("AGV {SerialNumber} map graph updated: {NodeCount} nodes, {EdgeCount} edges (MapId={MapId})",
            _config.SerialNumber,
            graph?.Nodes.Count ?? 0,
            graph?.Adjacency.Values.Sum(e => e.Count) ?? 0,
            graph?.MapId ?? "null");
    }

    /// <summary>Add or replace a map graph for a specific map. Supports multi-map operation.</summary>
    public void AddMapGraph(SimMapGraph graph)
    {
        if (graph == null || string.IsNullOrEmpty(graph.MapId)) return;
        _mapGraphs[graph.MapId] = graph;
        // Also set as primary if no primary graph yet
        _mapGraph ??= graph;
        TryPopulateLastNodeIdFromCurrentPose();
        _logger.LogInformation("AGV {SerialNumber} added map graph for MapId={MapId} ({NodeCount} nodes)",
            _config.SerialNumber, graph.MapId, graph.Nodes.Count);
    }

    private void TryPopulateLastNodeIdFromCurrentPose()
    {
        if (!string.IsNullOrWhiteSpace(_currentState.LastNodeId))
        {
            return;
        }

        _currentState.LastNodeId = ResolveNearestNodeIdOnCurrentMap() ?? string.Empty;
    }

    private string? ResolveNearestNodeIdOnCurrentMap()
    {
        var graph = ResolveCurrentMapGraph();
        if (graph == null || graph.Nodes.Count == 0)
        {
            return null;
        }

        string? nearestNodeId = null;
        var bestDistanceSquared = double.MaxValue;
        foreach (var node in graph.Nodes.Values)
        {
            var dx = node.X - _currentX;
            var dy = node.Y - _currentY;
            var distanceSquared = (dx * dx) + (dy * dy);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                nearestNodeId = node.NodeId;
            }
        }

        return nearestNodeId;
    }

    private SimMapGraph? ResolveCurrentMapGraph()
    {
        if (!string.IsNullOrWhiteSpace(_currentMapId) &&
            _mapGraphs.TryGetValue(_currentMapId, out var currentGraph))
        {
            return currentGraph;
        }

        if (_mapGraph != null &&
            (string.IsNullOrWhiteSpace(_mapGraph.MapId) ||
             string.IsNullOrWhiteSpace(_currentMapId) ||
             string.Equals(_mapGraph.MapId, _currentMapId, StringComparison.OrdinalIgnoreCase)))
        {
            return _mapGraph;
        }

        return null;
    }

    /// <summary>Set movement safety resolver to block motion when another AGV is too close/in front.</summary>
    public void SetMovementBlockResolver(
        Func<string, double, double, double, double, double, bool, string, string?> resolver)
    {
        _movementBlockResolver = resolver;
    }

    // ── Public Command APIs ──────────────────────────────────────────────

    public void SetSpeed(double speed)         => _commandChannel.Writer.TryWrite(new SetSpeedCmd(speed));
    public void SetBatteryLevel(double level)  => _commandChannel.Writer.TryWrite(new SetBatteryCmd(level));
    public void SetOperatingMode(string mode)  => _commandChannel.Writer.TryWrite(new SetOperatingModeCmd(mode));
    public void ClearOrderState() => _commandChannel.Writer.TryWrite(new ClearOrderStateCmd());
    public async Task LiftAsync()              { _commandChannel.Writer.TryWrite(new LiftCmd()); await Task.CompletedTask; }
    public async Task LowerAsync()             { _commandChannel.Writer.TryWrite(new LowerCmd()); await Task.CompletedTask; }

    /// <inheritdoc/>
    public IReadOnlyList<InboundMqttMessage> GetRecentInboundMessages()
    {
        lock (_inboundMessagesLock)
        {
            return _inboundMessages.ToList();
        }
    }

    /// <summary>Record a raw ACS MQTT payload for the control-page debug panel.</summary>
    private void RecordInboundMqttMessage(
        string topicType,
        string topic,
        string payload,
        bool accepted,
        string? note = null)
    {
        var entry = new InboundMqttMessage
        {
            Timestamp = DateTime.UtcNow,
            TopicType = topicType,
            Topic = topic,
            Payload = payload,
            Accepted = accepted,
            Note = note
        };

        lock (_inboundMessagesLock)
        {
            _inboundMessages.AddFirst(entry);
            while (_inboundMessages.Count > MaxInboundMqttMessages)
            {
                _inboundMessages.RemoveLast();
            }
        }
    }
    public async Task<int> ClearLoadsAsync()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commandChannel.Writer.TryWrite(new ClearLoadsCmd(completion)))
        {
            return -1;
        }

        return await completion.Task;
    }

    // ── Manual Position (Joystick) Control ───────────────────────────────

    /// <summary>Thread-safe: enqueues SetPositionCmd to event loop.</summary>
    public void SetPosition(double x, double y, double theta, string mapId)
    {
        // Update lastManualPositionUpdate BEFORE enqueueing so watchdog sees fresh timestamp
        _lastManualPositionUpdate = DateTime.UtcNow;
        _commandChannel.Writer.TryWrite(new SetPositionCmd(x, y, theta, ResolveInboundMapId(mapId)));
    }

    /// <summary>Internal: called inside event loop — no race condition.</summary>
    private void SetPositionInternal(double x, double y, double theta, string mapId)
    {
        var previousX = _currentX;
        var previousY = _currentY;
        var previousTheta = _currentTheta;
        var now = DateTime.UtcNow;

        ApplyPositionInternal(x, y, theta, mapId);
        _currentState.LastNodeId = ResolveNearestNodeIdOnCurrentMap() ?? string.Empty;
        UpdateManualVelocity(previousX, previousY, previousTheta, now);
        _lastManualPositionUpdate = now;
        StartManualPositionPublishing();
        _logger.LogInformation("AGV {SerialNumber} position set to ({X:F2}, {Y:F2}) on {MapId}",
            _config.SerialNumber, x, y, mapId);
    }

    private void ApplyInitializedPositionInternal(
        double x,
        double y,
        double theta,
        string mapId,
        string? lastNodeId)
    {
        ClearNavigationForPositionInitialization();
        ApplyPositionInternal(x, y, theta, mapId);
        _targetX = x;
        _targetY = y;
        _currentState.LastNodeId = string.IsNullOrWhiteSpace(lastNodeId)
            ? ResolveNearestNodeIdOnCurrentMap() ?? string.Empty
            : lastNodeId.Trim();
        _currentState.LastNodeSequenceId = 0;
        _currentState.DistanceSinceLastNode = 0;
        _currentState.AgvPosition!.PositionInitialized = true;
        _currentState.AgvPosition.LocalizationScore = 1.0;
        ZeroStoppedVelocities();
        _lastManualPoseAt = DateTime.MinValue;
        _lastManualPositionUpdate = DateTime.MinValue;
        _logger.LogInformation(
            "AGV {SerialNumber} initialized position to ({X:F2}, {Y:F2}) on {MapId} at node {LastNodeId}",
            _config.SerialNumber,
            x,
            y,
            mapId,
            _currentState.LastNodeId);
    }

    private void ClearNavigationForPositionInitialization()
    {
        _currentOrder = null;
        _currentNodeIndex = -1;
        StopActiveMovementForCompatibilityUpdate();
        _prevNodeId = string.Empty;
        _currentState.NodeStates.Clear();
        _currentState.EdgeStates.Clear();
        _currentState.NewBaseRequest = false;
    }

    private void ApplyPositionInternal(double x, double y, double theta, string mapId)
    {
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
        _currentVisualization.AgvPosition!.X = x;
        _currentVisualization.AgvPosition.Y = y;
        _currentVisualization.AgvPosition.Theta = ToServerTheta(theta);
        _currentVisualization.AgvPosition.MapId = mapId;
    }

    private string ResolveInboundMapId(string? mapId)
    {
        if (string.IsNullOrWhiteSpace(mapId))
        {
            return mapId ?? string.Empty;
        }

        var trimmed = mapId.Trim();
        return _mapIdMappings.TryGetValue(trimmed, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : trimmed;
    }

    /// <summary>
    /// Start visualization timer for manual position control (joystick).
    /// Auto-stops after ManualPositionTimeout of inactivity.
    /// </summary>
    private void StartManualPositionPublishing()
    {
        if (_manualPositionTimer != null)
            return;
        StartVisualizationTimer();
        // Watchdog enqueues ManualPositionStopCmd rather than mutating state directly
        _manualPositionTimer = new Timer(_ =>
        {
            if ((DateTime.UtcNow - _lastManualPositionUpdate) > ManualPositionTimeout)
                _commandChannel.Writer.TryWrite(ManualPositionStopCmd.Instance);
        }, null, ManualPositionTimeout, TimeSpan.FromMilliseconds(200));
        _logger.LogDebug("AGV {SerialNumber} started manual position publishing", _config.SerialNumber);
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
}
