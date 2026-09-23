using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using System.Text.Json;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — State management: publish state and visualization to MQTT, battery warning, sync order states.
/// </summary>
public partial class VirtualAgv
{
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
            var orderEdge = FindOrderEdge(prevNode, targetNode);
            if (orderEdge != null)
            {
                _currentState.EdgeStates.Add(new EdgeState
                {
                    EdId = orderEdge.EdgeId,
                    SequenceId = orderEdge.SequenceId,
                    Released = targetNode.Released
                });
            }
        }
        if (_currentOrder.Edges.Count > 0 && _currentNodeIndex < _currentOrder.Nodes.Count)
        {
            var edgeStartIndex = Math.Clamp(_currentNodeIndex, 0, Math.Max(0, _currentOrder.Nodes.Count - 1));
            var minimumSequenceId = _currentOrder.Nodes.Count > 0
                ? _currentOrder.Nodes[edgeStartIndex].SequenceId
                : 0;
            foreach (var edge in _currentOrder.Edges
                .Where(edge => edge.SequenceId >= minimumSequenceId)
                .OrderBy(edge => edge.SequenceId))
            {
                AddEdgeStateIfMissing(edge);
            }
        }
    }

    private void AddEdgeStateIfMissing(Edge edge)
    {
        if (_currentState.EdgeStates.Any(existing =>
                existing.SequenceId == edge.SequenceId &&
                string.Equals(existing.EdId, edge.EdgeId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _currentState.EdgeStates.Add(new EdgeState
        {
            EdId = edge.EdgeId,
            SequenceId = edge.SequenceId,
            Released = edge.Released,
            Trajectory = edge.Trajectory
        });
    }

    private async Task PublishStateAsync(bool immediate)
    {
        try
        {
            SyncOrderStates();
            // Battery simulation (fake idle charging when not driving + explicit charging) on periodic ticks
            if (!immediate && _batteryConfig.Enabled)
            {
                var now = DateTime.UtcNow;
                double elapsedMinutes = (now - _lastIdleDrainTime).TotalMinutes;
                _lastIdleDrainTime = now;

                if (_isCharging)
                {
                    _batteryLevel = Math.Min(100.0, _batteryLevel + _batteryConfig.ChargeRatePerMinute * elapsedMinutes);
                    _currentState.BatteryState.BatteryCharge = _batteryLevel;
                    _currentState.BatteryState.Reach = CalculateReach(_batteryLevel);
                    if (_batteryLevel >= 100.0)
                    {
                        _isCharging = false;
                        _currentState.BatteryState.Charging = false;
                        _logger.LogInformation("AGV {SerialNumber} stopped charging at full battery", _config.SerialNumber);
                    }
                    _logger.LogDebug("AGV {SerialNumber} charging: {Level:F1}%", _config.SerialNumber, _batteryLevel);
                }
                else if (!_isMoving && _batteryConfig.IdleTrickleChargeEnabled)
                {
                    // W10: off by default. A standing AGV recovering battery without any charge
                    // command is what kept the charge-freeze defect invisible in simulation.
                    _batteryLevel = Math.Min(
                        100.0,
                        _batteryLevel + _batteryConfig.IdleTrickleChargeRatePerMinute * elapsedMinutes);
                    _currentState.BatteryState.BatteryCharge = _batteryLevel;
                    _currentState.BatteryState.Reach = CalculateReach(_batteryLevel);
                    _logger.LogDebug(
                        "AGV {SerialNumber} idle trickle charging ({Rate:F2}%/min): {Level:F1}%",
                        _config.SerialNumber, _batteryConfig.IdleTrickleChargeRatePerMinute, _batteryLevel);
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

            if (string.IsNullOrWhiteSpace(_currentState.LastNodeId))
            {
                TryPopulateLastNodeIdFromCurrentPose();
            }

            _currentState.HeaderId = GetNextHeaderId();
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
                _logger.LogInformation("AGV {SerialNumber} published state (immediate) - Driving: {Driving}, Position: ({X:F2}, {Y:F2}), MapId: '{MapId}', Actions: {ActionCount}, Loads: {LoadCount}",
                    _config.SerialNumber,
                    _currentState.Driving,
                    _currentX,
                    _currentY,
                    _currentState.AgvPosition?.MapId ?? "",
                    _currentState.ActionStates.Count,
                    _currentState.Loads.Count);
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

    /// <summary>
    /// Clear linear/angular velocity on both VDA state and visualization after a stop.
    /// Prevents ACS proximity prediction from treating a paused AGV as still advancing.
    /// </summary>
    private void ZeroStoppedVelocities()
    {
        _currentState.Velocity ??= new Velocity();
        _currentState.Velocity.Vx = 0;
        _currentState.Velocity.Vy = 0;
        _currentState.Velocity.Omega = 0;

        _currentVisualization.Velocity ??= new Velocity();
        _currentVisualization.Velocity.Vx = 0;
        _currentVisualization.Velocity.Vy = 0;
        _currentVisualization.Velocity.Omega = 0;
    }

    /// <summary>
    /// Test seam: seed non-zero velocities then apply startPause without MQTT publish.
    /// Returns the JSON payload PublishStateAsync(true) would emit after the pause.
    /// </summary>
    internal async Task<(string StateJson, Velocity StateVelocity, Velocity VizVelocity)> ProcessStartPauseForTestsAsync(
        double seedVx = 1.5,
        double seedVy = -1.5,
        double seedOmega = 0.2)
    {
        SeedVelocityForTests(seedVx, seedVy, seedOmega);
        await ProcessInstantActionsAsync(new Vda5050InstantActions
        {
            Manufacturer = _config.Manufacturer,
            SerialNumber = _config.SerialNumber,
            Actions =
            [
                new VdaAction
                {
                    ActionType = InstantActionType.StartPause,
                    ActionId = "test-start-pause"
                }
            ]
        });
        // ProcessInstantActionsAsync already calls PublishStateAsync (may fail without broker).
        // Capture the same payload shape PublishStateAsync would serialize.
        SyncOrderStates();
        _currentState.HeaderId = GetNextHeaderId();
        _currentState.Timestamp = DateTime.UtcNow.ToString("o");
        var json = JsonSerializer.Serialize(_currentState);
        return (
            json,
            CloneVelocity(_currentState.Velocity),
            CloneVelocity(_currentVisualization.Velocity));
    }

    /// <summary>
    /// Test seam: seed non-zero velocities then apply emergency-stop side effect without MQTT.
    /// </summary>
    internal async Task<(string StateJson, Velocity StateVelocity, Velocity VizVelocity)> ProcessEmergencyStopForTestsAsync(
        double seedVx = 1.5,
        double seedVy = -1.5,
        double seedOmega = 0.2)
    {
        SeedVelocityForTests(seedVx, seedVy, seedOmega);
        await InjectErrorTemplateInternalAsync(new ErrorTemplate
        {
            Name = "EMERGENCY_STOP",
            ErrorType = "safety",
            ErrorLevel = "FATAL",
            Description = "Emergency stop triggered - eStop = MANUAL",
            SideEffect = ErrorSideEffect.EmergencyStop
        });
        SyncOrderStates();
        _currentState.HeaderId = GetNextHeaderId();
        _currentState.Timestamp = DateTime.UtcNow.ToString("o");
        var json = JsonSerializer.Serialize(_currentState);
        return (
            json,
            CloneVelocity(_currentState.Velocity),
            CloneVelocity(_currentVisualization.Velocity));
    }

    private void SeedVelocityForTests(double vx, double vy, double omega)
    {
        _currentState.Driving = true;
        _currentState.Paused = false;
        _currentState.Velocity ??= new Velocity();
        _currentState.Velocity.Vx = vx;
        _currentState.Velocity.Vy = vy;
        _currentState.Velocity.Omega = omega;
        _currentVisualization.Velocity ??= new Velocity();
        _currentVisualization.Velocity.Vx = vx;
        _currentVisualization.Velocity.Vy = vy;
        _currentVisualization.Velocity.Omega = omega;
    }

    /// <summary>Test seam: process a full order without MQTT/event-loop.</summary>
    internal Task ProcessOrderForTestsAsync(Vda5050Order order)
        => ProcessOrderAsync(order);

    /// <summary>Test seam: snapshot the assembled order after applying one or more updates.</summary>
    internal Vda5050Order? GetCurrentOrderForTests()
        => _currentOrder == null ? null : CloneIncomingOrder(_currentOrder);

    /// <summary>Test seam: the live state object, for asserting charging and error reporting.</summary>
    internal Vda5050State GetCurrentStateForTests() => _currentState;

    /// <summary>Test seam: visualization map id after applying simulator map translations.</summary>
    internal string GetCurrentVisualizationMapIdForTests() => _currentVisualization.AgvPosition?.MapId ?? string.Empty;

    /// <summary>Test seam: battery level, and a way to place the AGV at a chosen level.</summary>
    internal double BatteryLevelForTests
    {
        get => _batteryLevel;
        set
        {
            _batteryLevel = value;
            _currentState.BatteryState.BatteryCharge = value;
        }
    }

    /// <summary>Test seam: run one periodic battery/state tick with a controlled elapsed time.</summary>
    internal Task RunBatteryTickForTestsAsync(TimeSpan elapsed)
    {
        _lastIdleDrainTime = DateTime.UtcNow - elapsed;
        return PublishStateAsync(immediate: false);
    }

    /// <summary>Test seam: process a single instant action without MQTT.</summary>
    internal Task ProcessInstantActionForTestsAsync(string actionType, string actionId)
        => ProcessInstantActionsAsync(new Vda5050InstantActions
        {
            Manufacturer = _config.Manufacturer,
            SerialNumber = _config.SerialNumber,
            Actions =
            [
                new VdaAction
                {
                    ActionType = actionType,
                    ActionId = actionId
                }
            ]
        });

    /// <summary>Test seam: process a deserialized instant-action message without MQTT.</summary>
    internal Task ProcessInstantActionsForTestsAsync(Vda5050InstantActions instantActions)
        => ProcessInstantActionsAsync(instantActions);

    /// <summary>Test seam: start actor loop so MQTT-style command enqueue is serialized.</summary>
    internal Task StartActorForTestsAsync() => StartAsync();

    /// <summary>Test seam: stop actor loop after ingress tests.</summary>
    internal Task StopActorForTestsAsync() => StopAsync();

    /// <summary>Test seam: enqueue order through the actor channel (not direct ProcessOrderAsync).</summary>
    internal Task EnqueueOrderForTestsAsync(Vda5050Order order)
        => _commandChannel.Writer.WriteAsync(new ProcessOrderCmd(order)).AsTask();

    /// <summary>Test seam: mirror MQTT order ingress, including replacement cancellation.</summary>
    internal async Task EnqueueMqttOrderForTestsAsync(Vda5050Order order)
    {
        CancelActionForIncomingOrderIfReplacing(order);
        await _commandChannel.Writer.WriteAsync(new ProcessOrderCmd(order));
    }

    /// <summary>
    /// Test seam: mirror MQTT instantActions ingress — cancelOrder cancels action CTS;
    /// startPause only sets the atomic latch; actor owns ProcessInstantActions mutations.
    /// </summary>
    internal async Task EnqueueMqttInstantActionsForTestsAsync(string actionType, string actionId)
    {
        var isCancel = actionType.Equals(InstantActionType.CancelOrder, StringComparison.OrdinalIgnoreCase) ||
                       actionType.Equals("cancelOrder", StringComparison.OrdinalIgnoreCase);
        var isStartPause = actionType.Equals(InstantActionType.StartPause, StringComparison.OrdinalIgnoreCase) ||
                           actionType.Equals("startPause", StringComparison.OrdinalIgnoreCase) ||
                           actionType.Equals("pause", StringComparison.OrdinalIgnoreCase);
        var isStopPause = actionType.Equals(InstantActionType.StopPause, StringComparison.OrdinalIgnoreCase) ||
                          actionType.Equals("stopPause", StringComparison.OrdinalIgnoreCase) ||
                          actionType.Equals("resume", StringComparison.OrdinalIgnoreCase);

        if (isCancel)
        {
            _actionCts?.Cancel();
        }

        if (isStartPause)
        {
            _pauseLatched = true;
        }
        else if (isStopPause)
        {
            _pauseLatched = false;
        }

        await _commandChannel.Writer.WriteAsync(new ProcessInstantActionsCmd(new Vda5050InstantActions
        {
            Manufacturer = _config.Manufacturer,
            SerialNumber = _config.SerialNumber,
            Actions =
            [
                new VdaAction
                {
                    ActionType = actionType,
                    ActionId = actionId
                }
            ]
        }));
    }

    internal void CancelActionTokenForTests()
        => _actionCts?.Cancel();

    internal string? GetActionStatusForTests(string actionId)
        => _currentState.ActionStates
            .FirstOrDefault(a => string.Equals(a.ActionId, actionId, StringComparison.OrdinalIgnoreCase))
            ?.ActionStatus;

    internal bool GetPausedForTests() => _currentState.Paused == true;

    internal bool GetDrivingForTests() => _currentState.Driving;

    internal bool GetPauseLatchedForTests() => _pauseLatched;

    internal IReadOnlyList<Load> GetLoadsForTests()
        => _currentState.Loads.ToList();

    private static Velocity CloneVelocity(Velocity? source)
        => new()
        {
            Vx = source?.Vx ?? 0,
            Vy = source?.Vy ?? 0,
            Omega = source?.Omega ?? 0
        };

    private async Task PublishVisualizationAsync()
    {
        try
        {
            _currentVisualization.HeaderId = GetNextHeaderId();
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
}
