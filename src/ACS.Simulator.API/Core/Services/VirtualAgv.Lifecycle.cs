using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — Lifecycle: startup, shutdown, actor event loop, command dispatch, visualization timers.
/// </summary>
public partial class VirtualAgv
{
    public async Task StartAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        _loopCts = new CancellationTokenSource();

        // Timers only enqueue commands — no direct state mutation
        _statePublishTimer = new Timer(
            _ => _commandChannel.Writer.TryWrite(new StatePublishTickCmd(false)),
            null,
            TimeSpan.FromMilliseconds(_statePublishConfig.PeriodicInterval),
            TimeSpan.FromMilliseconds(_statePublishConfig.PeriodicInterval));

        _movementTimer = new Timer(
            _ => _commandChannel.Writer.TryWrite(MovementTickCmd.Instance),
            null,
            TimeSpan.FromMilliseconds(_movementConfig.MovementUpdateInterval),
            TimeSpan.FromMilliseconds(_movementConfig.MovementUpdateInterval));

        // Single event loop — all state mutations happen here, eliminating races
        _eventLoopTask = Task.Run(() => RunEventLoopAsync(_loopCts.Token));

        _logger.LogInformation("AGV {SerialNumber} started (actor-loop) - State: {StatePeriod}ms, Movement: {MovementPeriod}ms",
            _config.SerialNumber, _statePublishConfig.PeriodicInterval, _movementConfig.MovementUpdateInterval);
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _isRunning = false;

        _chaosReconnectCts?.Cancel();
        var reconnectTask = _chaosReconnectTask;
        if (reconnectTask is { IsCompleted: false })
        {
            try { await reconnectTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                _logger.LogWarning("AGV {SerialNumber} chaos reconnect task did not stop in 2s", _config.SerialNumber);
            }
            catch (OperationCanceledException) { }
        }

        await DisconnectAsync();

        // Stop timers first — no more commands will be enqueued
        _statePublishTimer?.Dispose(); _statePublishTimer = null;
        _movementTimer?.Dispose();     _movementTimer = null;
        _manualPositionTimer?.Dispose(); _manualPositionTimer = null;
        StopVisualizationTimer();

        // Complete channel so the event loop exits after draining remaining commands
        _commandChannel.Writer.TryComplete();

        if (_eventLoopTask != null)
        {
            try { await _eventLoopTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                _logger.LogWarning("AGV {SerialNumber} event loop did not stop in 5s — cancelling", _config.SerialNumber);
                _loopCts?.Cancel();
            }
            catch (OperationCanceledException) { }
        }

        _loopCts?.Dispose(); _loopCts = null;
        _chaosReconnectCts?.Dispose(); _chaosReconnectCts = null;
        _chaosReconnectTask = null;
        _eventLoopTask = null;
        _logger.LogInformation("AGV {SerialNumber} stopped", _config.SerialNumber);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _statePublishTimer?.Dispose();
        _movementTimer?.Dispose();
        _manualPositionTimer?.Dispose();
        StopVisualizationTimer();
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _actionCts?.Dispose();
        _chaosReconnectCts?.Cancel();
        _chaosReconnectCts?.Dispose();
        DetachMqttHandler();
        _mqttClient?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ── Actor Event Loop ──────────────────────────────────────────────────

    private async Task RunEventLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("AGV {SerialNumber} event loop started", _config.SerialNumber);
        try
        {
            await foreach (var cmd in _commandChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await DispatchCommandAsync(cmd);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // Action was cancelled by a new order — normal, continue loop
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AGV {SerialNumber} event loop error on {Cmd}",
                        _config.SerialNumber, cmd.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _logger.LogInformation("AGV {SerialNumber} event loop stopped", _config.SerialNumber);
        }
    }

    private async Task DispatchCommandAsync(AgvCommand cmd)
    {
        switch (cmd)
        {
            case MovementTickCmd:
                await UpdateMovementAsync();
                break;

            case StatePublishTickCmd s:
                await PublishStateAsync(s.Immediate);
                break;

            case VisualizationTickCmd:
                await PublishVisualizationAsync();
                break;

            case ProcessOrderCmd o:
                // MANUAL: forget prior mission and wait for ACS to re-send after returning to AUTOMATIC.
                if (IsManualOperatingMode())
                {
                    _logger.LogInformation(
                        "AGV {SerialNumber} ignored order while operatingMode=MANUAL: orderId={OrderId}, updateId={UpdateId}",
                        _config.SerialNumber, o.Order.OrderId, o.Order.OrderUpdateId);
                    break;
                }

                // ProcessOrderAsync owns action CTS create/replace for the new order.
                await ProcessOrderAsync(o.Order);
                break;

            case ProcessInstantActionsCmd ia:
                if (IsManualOperatingMode())
                {
                    // Still allow cancelOrder / pause-style control in MANUAL; navigation orders stay blocked above.
                    _logger.LogInformation(
                        "AGV {SerialNumber} processing instantActions while operatingMode=MANUAL ({Count} action(s))",
                        _config.SerialNumber, ia.Actions.Actions?.Count ?? 0);
                }

                // Do not dispose/replace the order action CTS here. startPause/stopPause must not
                // own the pick/drop token; cancelOrder cancels via MQTT + ClearMissionState.
                await ProcessInstantActionsAsync(ia.Actions);
                break;

            case SetOperatingModeCmd m:
                await SetOperatingModeInternalAsync(m.Mode);
                break;

            case ClearOrderStateCmd:
                await ClearOrderStateInternalAsync();
                break;

            case SetPositionCmd s:
                SetPositionInternal(s.X, s.Y, s.Theta, s.MapId);
                break;

            case SetBatteryCmd b:
                _batteryLevel = Math.Clamp(b.Level, 0, 100);
                _currentState.BatteryState.BatteryCharge = _batteryLevel;
                _currentState.BatteryState.Reach = CalculateReach(_batteryLevel);
                break;

            case SetSpeedCmd s:
                _movementConfig.Speed = s.Speed;
                _logger.LogInformation("AGV {SerialNumber} speed updated to {Speed} m/s", _config.SerialNumber, s.Speed);
                break;

            case SetChaosCmd c:
                _chaosMinLatencyMs = Math.Max(0, c.MinMs);
                _chaosMaxLatencyMs = Math.Max(0, c.MaxMs);
                _chaosPacketLossPercent = Math.Clamp(c.LossPercent, 0, 100);
                break;

            case AddErrorCmd a:
                _currentState.Errors.Add(new VdaError
                {
                    ErrorType = a.ErrorType,
                    ErrorLevel = a.ErrorLevel,
                    ErrorDescription = a.Description
                });
                _isMoving = false;
                _isRotating = false;
                _currentState.Driving = false;
                ZeroStoppedVelocities();
                StopVisualizationTimer();
                _logger.LogWarning("AGV {SerialNumber} error added: {Type} [{Level}] -> Stopped movement",
                    _config.SerialNumber, a.ErrorType, a.ErrorLevel);
                await PublishStateAsync(true);
                break;

            case ClearErrorCmd c:
                if (c.ErrorType == null)
                {
                    _currentState.Errors.Clear();
                    _currentState.SafetyState.EStop = "NONE";
                    _currentState.SafetyState.FieldViolation = false;
                    _currentState.Paused = false;
                    _pauseLatched = false;
                    if (_currentState.AgvPosition != null)
                    {
                        _currentState.AgvPosition.PositionInitialized = true;
                        _currentState.AgvPosition.LocalizationScore = 1.0;
                    }
                }
                else
                {
                    _currentState.Errors.RemoveAll(e =>
                        e.ErrorType.Equals(c.ErrorType, StringComparison.OrdinalIgnoreCase));

                    if (c.ErrorType.Equals("safety", StringComparison.OrdinalIgnoreCase) ||
                        c.ErrorType.Equals("EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase))
                    {
                        _currentState.SafetyState.EStop = "NONE";
                        _currentState.SafetyState.FieldViolation = false;
                        _currentState.Paused = false;
                    }

                    if (c.ErrorType.Equals("localization", StringComparison.OrdinalIgnoreCase) ||
                        c.ErrorType.Equals("LOCALIZATION_LOST", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_currentState.AgvPosition != null)
                        {
                            _currentState.AgvPosition.PositionInitialized = true;
                            _currentState.AgvPosition.LocalizationScore = 1.0;
                        }
                    }
                }

                _logger.LogInformation("AGV {SerialNumber} error cleared: {Type} (Reset EStop to NONE, fieldViolation=false)",
                    _config.SerialNumber, c.ErrorType ?? "ALL");
                await PublishStateAsync(true);

                if (_currentState.Errors.Count == 0 && !_pauseLatched && !_currentState.Paused && (_currentState.SafetyState?.EStop ?? "NONE") == "NONE")
                {
                    await TryResumeMovementAsync();
                }
                break;

            case InjectErrorTemplateCmd it:
                await InjectErrorTemplateInternalAsync(it.Template);
                break;

            case LiftCmd:
                await LiftInternalAsync();
                break;

            case LowerCmd:
                await LowerInternalAsync();
                break;

            case ClearLoadsCmd c:
                var clearedLoads = _currentState.Loads.Count;
                _currentState.Loads.Clear();
                _hasLoad = false;
                _logger.LogInformation("AGV {SerialNumber} cleared {LoadCount} load(s) from state",
                    _config.SerialNumber, clearedLoads);
                await PublishStateAsync(true);
                c.Completion.TrySetResult(clearedLoads);
                break;

            case ClearEmergencyStopCmd:
                _currentState.SafetyState.EStop = "NONE";
                _currentState.SafetyState.FieldViolation = false;
                _currentState.Paused = false;
                _pauseLatched = false;
                _currentState.Errors.RemoveAll(e =>
                    e.ErrorType.Equals("safety", StringComparison.OrdinalIgnoreCase) ||
                    e.ErrorType.Equals("EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase));
                _logger.LogInformation("AGV {SerialNumber} emergency stop cleared", _config.SerialNumber);
                await PublishStateAsync(true);

                if (_currentState.Errors.Count == 0 && !_pauseLatched)
                {
                    await TryResumeMovementAsync();
                }
                break;

            case RestoreLocalizationCmd:
                if (_currentState.AgvPosition != null)
                {
                    _currentState.AgvPosition.PositionInitialized = true;
                    _currentState.AgvPosition.LocalizationScore = 1.0;
                }
                _currentState.Errors.RemoveAll(e =>
                    e.ErrorType.Equals("localization", StringComparison.OrdinalIgnoreCase) ||
                    e.ErrorType.Equals("LOCALIZATION_LOST", StringComparison.OrdinalIgnoreCase));
                _logger.LogInformation("AGV {SerialNumber} localization restored", _config.SerialNumber);
                await PublishStateAsync(true);

                if (_currentState.Errors.Count == 0 && !_pauseLatched && !_currentState.Paused)
                {
                    await TryResumeMovementAsync();
                }
                break;

            case ManualPositionStopCmd:
                _manualPositionTimer?.Dispose();
                _manualPositionTimer = null;
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
                break;
        }
    }

    // ── Visualization Timer Helpers ───────────────────────────────────────

    private void StartVisualizationTimer()
    {
        if (_visualizationTimer != null)
            return;

        // Enqueue visualization command — processed by single event loop, no concurrent publish
        _visualizationTimer = new Timer(
            _ => _commandChannel.Writer.TryWrite(VisualizationTickCmd.Instance),
            null,
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
}
