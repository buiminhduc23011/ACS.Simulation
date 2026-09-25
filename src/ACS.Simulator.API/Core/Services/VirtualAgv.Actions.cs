using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// VirtualAgv — Actions: execute PICK/DROP/WAIT node actions, lift/lower load, error management,
/// emergency stop, localization restore.
/// </summary>
public partial class VirtualAgv
{
    private async Task ExecuteActionAsync(VdaAction action)
    {
        _logger.LogInformation("AGV {SerialNumber} executing action {ActionType} (id: {ActionId})",
            _config.SerialNumber, action.ActionType, action.ActionId);

        var ct = _actionCts?.Token ?? CancellationToken.None;

        var actionState = UpsertActionState(action, "WAITING");
        await PublishStateAsync(true);

        try
        {
            actionState.ActionStatus = "RUNNING";
            await PublishStateAsync(true);
            Func<Task>? applyLoadStateAfterFinished = null;

            if (GetActionParameterBool(action, "simFail"))
            {
                throw new InvalidOperationException("Simulated action failure");
            }

            switch (action.ActionType.ToUpper())
            {
                case "PICK":
                case "LIFT":
                    await Task.Delay(_actionsConfig.LiftDurationMs, ct);
                    applyLoadStateAfterFinished = async () =>
                    {
                        await Task.Delay(_actionsConfig.LoadStateDelayMs, ct);
                        _hasLoad = true;
                        var loadId = GetActionParameterString(action, "loadId") ?? $"LOAD_{DateTime.UtcNow.Ticks}";
                        if (GetActionParameterBool(action, "simLoadMismatch"))
                        {
                            loadId = $"MISMATCH_{DateTime.UtcNow.Ticks}";
                        }

                        _currentState.Loads.Add(new Load
                        {
                            LoadId = loadId,
                            LoadType = GetActionParameterString(action, "loadType") ?? "pallet",
                            LoadPosition = GetActionParameterString(action, "loadPosition") ?? "center",
                            Weight = 100.0
                        });
                        _logger.LogInformation("AGV {SerialNumber} picked up load {LoadId}", _config.SerialNumber, loadId);
                    };
                    break;

                case "DROP":
                case "LOWER":
                    await Task.Delay(_actionsConfig.LowerDurationMs, ct);
                    applyLoadStateAfterFinished = async () =>
                    {
                        await Task.Delay(_actionsConfig.LoadStateDelayMs, ct);
                        var loadId = GetActionParameterString(action, "loadId");
                        var loadType = GetActionParameterString(action, "loadType");
                        var loadPosition = GetActionParameterString(action, "loadPosition");

                        if (!string.IsNullOrWhiteSpace(loadId))
                        {
                            _currentState.Loads.RemoveAll(load =>
                                string.Equals(load.LoadId, loadId, StringComparison.OrdinalIgnoreCase));
                        }
                        else if (!string.IsNullOrWhiteSpace(loadType) || !string.IsNullOrWhiteSpace(loadPosition))
                        {
                            _currentState.Loads.RemoveAll(load =>
                                (string.IsNullOrWhiteSpace(loadType) || string.Equals(load.LoadType, loadType, StringComparison.OrdinalIgnoreCase)) &&
                                (string.IsNullOrWhiteSpace(loadPosition) || string.Equals(load.LoadPosition, loadPosition, StringComparison.OrdinalIgnoreCase)));
                        }
                        else
                        {
                            _currentState.Loads.Clear();
                        }

                        _hasLoad = _currentState.Loads.Count > 0;
                        _logger.LogInformation("AGV {SerialNumber} dropped load", _config.SerialNumber);
                    };
                    break;

                case "WAIT":
                    var durationParam = action.ActionParameters?.FirstOrDefault(p => p.Key == "duration");
                    int waitMs = durationParam != null ? Convert.ToInt32(durationParam.Value) : 1000;
                    await Task.Delay(waitMs, ct);
                    _logger.LogInformation("AGV {SerialNumber} waited {Duration}ms", _config.SerialNumber, waitMs);
                    break;

                case "ACS.DISABLESAFETYFRONT":
                case "ACS.DISABLESAFETYREAR":
                    actionState.ResultDescription = "Simulator debug acknowledgement; no LiDAR behavior is simulated.";
                    _logger.LogInformation(
                        "AGV {SerialNumber} observed ACS safety action {ActionType} for debug only; simulated hardware is unchanged.",
                        _config.SerialNumber,
                        action.ActionType);
                    break;

                default:
                    _logger.LogWarning("AGV {SerialNumber} unknown action type {ActionType}",
                        _config.SerialNumber, action.ActionType);
                    break;
            }

            actionState.ActionStatus = "FINISHED";
            _logger.LogInformation("AGV {SerialNumber} finished action {ActionType}", _config.SerialNumber, action.ActionType);
            await PublishStateAsync(true);

            if (applyLoadStateAfterFinished != null)
            {
                await applyLoadStateAfterFinished();
            }
        }
        catch (OperationCanceledException)
        {
            // Action cancelled by cancelOrder, new order replacement, or EStop — not by startPause.
            actionState.ActionStatus = "FAILED";
            actionState.ResultDescription = "Cancelled by cancelOrder or emergency stop";
            _logger.LogInformation("AGV {SerialNumber} action {ActionType} cancelled", _config.SerialNumber, action.ActionType);
            await PublishStateAsync(true);
            throw;
        }
        catch (Exception ex)
        {
            actionState.ActionStatus = "FAILED";
            _logger.LogError(ex, "AGV {SerialNumber} failed action {ActionType}", _config.SerialNumber, action.ActionType);
        }

        await PublishStateAsync(true);
    }

    private static string? GetActionParameterString(VdaAction action, string key)
        => action.ActionParameters?
            .FirstOrDefault(parameter => string.Equals(parameter.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.ToString();

    private static bool GetActionParameterBool(VdaAction action, string key)
    {
        var raw = GetActionParameterString(action, key);
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    // Internal: runs inside event loop
    private async Task LiftInternalAsync()
    {
        var ct = _actionCts?.Token ?? CancellationToken.None;
        var action = FindCurrentNodeLoadAction("PICK", "LIFT");
        if (action != null)
        {
            UpsertActionState(action, "RUNNING").ResultDescription = "Completed by simulator lift control";
            await PublishStateAsync(true);
        }

        try { await Task.Delay(_actionsConfig.LiftDurationMs, ct); } catch (OperationCanceledException) { return; }
        _hasLoad = true;
        _currentState.Loads.Add(new Load
        {
            LoadId = action == null ? $"LOAD_{DateTime.UtcNow.Ticks}" : GetActionParameterString(action, "loadId") ?? $"LOAD_{DateTime.UtcNow.Ticks}",
            LoadType = action == null ? "pallet" : GetActionParameterString(action, "loadType") ?? "pallet",
            LoadPosition = action == null ? "center" : GetActionParameterString(action, "loadPosition") ?? "center",
            Weight = 100.0
        });
        if (action != null)
        {
            UpsertActionState(action, "FINISHED").ResultDescription = "Completed by simulator lift control";
        }

        _logger.LogInformation("AGV {SerialNumber} lift complete", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    private async Task LowerInternalAsync()
    {
        var ct = _actionCts?.Token ?? CancellationToken.None;
        var action = FindCurrentNodeLoadAction("DROP", "LOWER");
        if (action != null)
        {
            UpsertActionState(action, "RUNNING").ResultDescription = "Completed by simulator lower control";
            await PublishStateAsync(true);
        }

        try { await Task.Delay(_actionsConfig.LowerDurationMs, ct); } catch (OperationCanceledException) { return; }
        if (action == null)
        {
            _currentState.Loads.Clear();
        }
        else
        {
            RemoveLoadsForAction(action);
            UpsertActionState(action, "FINISHED").ResultDescription = "Completed by simulator lower control";
        }

        _hasLoad = _currentState.Loads.Count > 0;
        _logger.LogInformation("AGV {SerialNumber} lower complete", _config.SerialNumber);
        await PublishStateAsync(true);
    }

    private ActionState UpsertActionState(VdaAction action, string status)
    {
        var actionState = _currentState.ActionStates.FirstOrDefault(existing =>
            string.Equals(existing.ActionId, action.ActionId, StringComparison.OrdinalIgnoreCase));
        if (actionState == null)
        {
            actionState = new ActionState
            {
                ActionId = action.ActionId,
                ActionType = action.ActionType,
                ActionDescription = action.ActionDescription
            };
            _currentState.ActionStates.Add(actionState);
        }

        actionState.ActionStatus = status;
        return actionState;
    }

    private VdaAction? FindCurrentNodeLoadAction(params string[] actionTypes)
    {
        if (_currentOrder == null ||
            _currentNodeIndex < 0 ||
            _currentNodeIndex >= _currentOrder.Nodes.Count)
        {
            return null;
        }

        var node = _currentOrder.Nodes[_currentNodeIndex];
        return node.Actions?.LastOrDefault(action =>
            actionTypes.Any(type => string.Equals(action.ActionType, type, StringComparison.OrdinalIgnoreCase)));
    }

    private void RemoveLoadsForAction(VdaAction action)
    {
        var loadId = GetActionParameterString(action, "loadId");
        var loadType = GetActionParameterString(action, "loadType");
        var loadPosition = GetActionParameterString(action, "loadPosition");

        if (!string.IsNullOrWhiteSpace(loadId))
        {
            _currentState.Loads.RemoveAll(load =>
                string.Equals(load.LoadId, loadId, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrWhiteSpace(loadType) || !string.IsNullOrWhiteSpace(loadPosition))
        {
            _currentState.Loads.RemoveAll(load =>
                (string.IsNullOrWhiteSpace(loadType) || string.Equals(load.LoadType, loadType, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(loadPosition) || string.Equals(load.LoadPosition, loadPosition, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            _currentState.Loads.Clear();
        }
    }

    // ── Error Management ─────────────────────────────────────────────────

    /// <summary>
    /// Add an error to the AGV state. Error will be sent with the next state publish.
    /// </summary>
    /// <param name="errorType">Type of error (e.g., "HARDWARE", "NAVIGATION", "COMMUNICATION")</param>
    /// <param name="errorLevel">Level of error: "WARNING", "FATAL"</param>
    /// <param name="description">Optional description of the error</param>
    public async Task AddErrorAsync(string errorType, string errorLevel = "WARNING", string? description = null)
    {
        _commandChannel.Writer.TryWrite(new AddErrorCmd(errorType, errorLevel, description));
        await Task.CompletedTask;
    }

    public async Task<bool> ClearErrorAsync(string errorType)
    {
        bool exists = _currentState.Errors.Any(e => e.ErrorType.Equals(errorType, StringComparison.OrdinalIgnoreCase));
        _commandChannel.Writer.TryWrite(new ClearErrorCmd(errorType));
        await Task.CompletedTask;
        return exists;
    }

    public async Task<int> ClearAllErrorsAsync()
    {
        int count = _currentState.Errors.Count;
        _commandChannel.Writer.TryWrite(new ClearErrorCmd(null));
        await Task.CompletedTask;
        return count;
    }

    /// <summary>Inject a predefined error template with its side-effect applied.</summary>
    public async Task InjectErrorTemplateAsync(ErrorTemplate template)
    {
        _commandChannel.Writer.TryWrite(new InjectErrorTemplateCmd(template));
        await Task.CompletedTask;
    }

    // Internal version runs inside event loop
    private async Task InjectErrorTemplateInternalAsync(ErrorTemplate template)
    {
        _logger.LogWarning("AGV {SerialNumber} injecting error template: {Name} (level: {Level})",
            _config.SerialNumber, template.Name, template.ErrorLevel);
        _currentState.Errors.RemoveAll(e => e.ErrorType == template.ErrorType);
        _currentState.Errors.Add(new VdaError
        {
            ErrorType = template.ErrorType, ErrorLevel = template.ErrorLevel, ErrorDescription = template.Description
        });
        // Any error injection immediately stops movement
        _isMoving = false;
        _isRotating = false;
        _currentState.Driving = false;
        ZeroStoppedVelocities();
        StopVisualizationTimer();

        switch (template.SideEffect)
        {
            case ErrorSideEffect.ReduceSpeed50Percent:
                _movementConfig.Speed *= 0.5;
                _logger.LogWarning("AGV {SerialNumber} speed reduced to {Speed} m/s and stopped due to error", _config.SerialNumber, _movementConfig.Speed);
                break;
            case ErrorSideEffect.StopMovement:
                _logger.LogWarning("AGV {SerialNumber} movement stopped due to error", _config.SerialNumber);
                break;
            case ErrorSideEffect.EmergencyStop:
                _currentState.Paused = true;
                _currentState.SafetyState.EStop = "MANUAL";
                _currentState.SafetyState.FieldViolation = true;
                _actionCts?.Cancel();
                _logger.LogCritical("AGV {SerialNumber} EMERGENCY STOP activated", _config.SerialNumber);
                break;
            case ErrorSideEffect.LocalizationLost:
                _currentState.AgvPosition!.PositionInitialized = false;
                _currentState.AgvPosition.LocalizationScore = 0.0;
                _logger.LogCritical("AGV {SerialNumber} localization LOST", _config.SerialNumber);
                break;
            case ErrorSideEffect.ClearLoads:
                _hasLoad = false;
                _currentState.Loads.Clear();
                _logger.LogCritical("AGV {SerialNumber} loads CLEARED due to drop", _config.SerialNumber);
                break;
        }
        await PublishStateAsync(true);
    }

    public async Task ClearEmergencyStopAsync()
    {
        _commandChannel.Writer.TryWrite(new ClearEmergencyStopCmd());
        await Task.CompletedTask;
    }

    public async Task RestoreLocalizationAsync()
    {
        _commandChannel.Writer.TryWrite(new RestoreLocalizationCmd());
        await Task.CompletedTask;
    }

    /// <summary>Get current errors list</summary>
    public IReadOnlyList<VdaError> GetErrors() => _currentState.Errors.AsReadOnly();

    /// <summary>Check if AGV has any errors</summary>
    public bool HasErrors => _currentState.Errors.Count > 0;

    /// <summary>Check if AGV has any fatal errors</summary>
    public bool HasFatalErrors => _currentState.Errors.Any(e =>
        e.ErrorLevel.Equals("FATAL", StringComparison.OrdinalIgnoreCase));
}
