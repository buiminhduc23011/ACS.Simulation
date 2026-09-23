using ACS.Simulator.API.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ACS.Simulator.API.Core.Services;

/// <summary>
/// Scenario Engine - thực thi các scenario test tự động.
/// Load scenario từ JSON file, execute từng step, report kết quả.
/// </summary>
public class ScenarioEngine
{
    private readonly ILogger<ScenarioEngine> _logger;
    private Func<string, IVirtualAgv?> _agvResolver;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public event EventHandler<ScenarioStepResult>? StepCompleted;
    public event EventHandler<string>? LogMessage;

    public bool IsRunning { get; private set; }

    public ScenarioEngine(ILogger<ScenarioEngine> logger, Func<string, IVirtualAgv?> agvResolver)
    {
        _logger = logger;
        _agvResolver = agvResolver;
    }

    public void UpdateAgvResolver(Func<string, IVirtualAgv?> resolver)
    {
        _agvResolver = resolver;
    }

    /// <summary>Load scenario from a JSON file path</summary>
    public static async Task<Scenario?> LoadFromFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<Scenario>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    /// <summary>Load all scenarios from a directory</summary>
    public static async Task<List<(string FilePath, Scenario Scenario)>> LoadAllFromDirectoryAsync(string directory)
    {
        var result = new List<(string, Scenario)>();
        if (!Directory.Exists(directory)) return result;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var scenario = await LoadFromFileAsync(file);
            if (scenario != null)
                result.Add((file, scenario));
        }
        return result;
    }

    /// <summary>Run a scenario. Returns run result with pass/fail per step.</summary>
    public async Task<ScenarioRunResult> RunAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        IsRunning = true;
        var startTime = DateTime.UtcNow;
        var stepResults = new List<ScenarioStepResult>();

        Emit($"Running scenario: {scenario.Name}");
        Emit($"  {scenario.Description}");
        Emit($"  Steps: {scenario.Steps.Count}");

        try
        {
            foreach (var step in scenario.Steps)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Emit("Scenario cancelled.");
                    break;
                }

                var stepStart = DateTime.UtcNow;
                string comment = step.Comment ?? step.Type;
                Emit($"\n[{stepResults.Count + 1}/{scenario.Steps.Count}] {step.Type}" + (step.Comment != null ? $" - {step.Comment}" : ""));

                ScenarioStepResult result;
                try
                {
                    var (passed, message) = await ExecuteStepAsync(step, cancellationToken);
                    result = new ScenarioStepResult(step.Type, step.Comment, passed, message, DateTime.UtcNow - stepStart);

                    if (passed)
                        Emit($"  PASS{(message != null ? $": {message}" : "")}");
                    else
                        Emit($"  FAIL: {message}");
                }
                catch (OperationCanceledException)
                {
                    result = new ScenarioStepResult(step.Type, step.Comment, false, "Cancelled", DateTime.UtcNow - stepStart);
                    Emit("  Cancelled");
                    stepResults.Add(result);
                    break;
                }
                catch (Exception ex)
                {
                    result = new ScenarioStepResult(step.Type, step.Comment, false, ex.Message, DateTime.UtcNow - stepStart);
                    Emit($"  Exception: {ex.Message}");
                }

                stepResults.Add(result);
                StepCompleted?.Invoke(this, result);
            }
        }
        finally
        {
            IsRunning = false;
        }

        var endTime = DateTime.UtcNow;
        var runResult = new ScenarioRunResult(scenario.Name, startTime, endTime, stepResults);

        Emit($"\n==================================");
        Emit($"Scenario: {scenario.Name}");
        Emit($"   Result:  {(runResult.Passed ? "PASSED" : "FAILED")}");
        Emit($"   Steps:   {runResult.PassedSteps}/{runResult.TotalSteps} passed");
        Emit($"   Time:    {runResult.TotalDuration.TotalSeconds:F1}s");
        Emit($"==================================");

        return runResult;
    }

    private async Task<(bool Passed, string? Message)> ExecuteStepAsync(ScenarioStep step, CancellationToken ct)
    {
        switch (step)
        {
            case WaitStep wait:
                Emit($"  Waiting {wait.DurationMs}ms...");
                await Task.Delay(wait.DurationMs, ct);
                return (true, $"Waited {wait.DurationMs}ms");

            case LogStep log:
                Emit($"  {log.Message}");
                return (true, log.Message);

            case SetBatteryStep setBattery:
            {
                var agv = ResolveAgv(setBattery.AgvId);
                if (agv == null) return (false, $"AGV '{setBattery.AgvId}' not found");
                agv.SetBatteryLevel(setBattery.Level);
                return (true, $"Battery set to {setBattery.Level:F1}% on {setBattery.AgvId}");
            }

            case SetPositionStep setPos:
            {
                var agv = ResolveAgv(setPos.AgvId);
                if (agv == null) return (false, $"AGV '{setPos.AgvId}' not found");
                agv.SetPosition(setPos.X, setPos.Y, setPos.Theta, setPos.MapId);
                return (true, $"{setPos.AgvId} teleported to ({setPos.X}, {setPos.Y})");
            }

            case SetSpeedStep setSpeed:
            {
                var agv = ResolveAgv(setSpeed.AgvId);
                if (agv == null) return (false, $"AGV '{setSpeed.AgvId}' not found");
                agv.SetSpeed(setSpeed.Speed);
                return (true, $"{setSpeed.AgvId} speed set to {setSpeed.Speed} m/s");
            }

            case InjectErrorStep inject:
            {
                var agv = ResolveAgv(inject.AgvId);
                if (agv == null) return (false, $"AGV '{inject.AgvId}' not found");
                var template = ErrorTemplates.GetByName(inject.TemplateName);
                if (template == null) return (false, $"Error template '{inject.TemplateName}' not found");
                await agv.InjectErrorTemplateAsync(template);
                return (true, $"Injected '{inject.TemplateName}' on {inject.AgvId}");
            }

            case ClearErrorStep clear:
            {
                var agv = ResolveAgv(clear.AgvId);
                if (agv == null) return (false, $"AGV '{clear.AgvId}' not found");
                if (clear.ErrorType == null)
                    await agv.ClearAllErrorsAsync();
                else
                    await agv.ClearErrorAsync(clear.ErrorType);
                return (true, $"Errors cleared on {clear.AgvId}");
            }

            case DisconnectStep disconnect:
            {
                var agv = ResolveAgv(disconnect.AgvId);
                if (agv == null) return (false, $"AGV '{disconnect.AgvId}' not found");
                await agv.TriggerDisconnectAsync(disconnect.DurationMs);
                return (true, $"{disconnect.AgvId} disconnected for {disconnect.DurationMs}ms");
            }

            case SetChaosStep chaos:
            {
                var agv = ResolveAgv(chaos.AgvId);
                if (agv == null) return (false, $"AGV '{chaos.AgvId}' not found");
                agv.SetChaosLatency(chaos.LatencyMinMs, chaos.LatencyMaxMs);
                agv.SetPacketLoss(chaos.PacketLossPercent);
                return (true, $"Chaos set on {chaos.AgvId}: latency={chaos.LatencyMinMs}-{chaos.LatencyMaxMs}ms, loss={chaos.PacketLossPercent}%");
            }

            case AssertStep assert:
                return await EvaluateAssertAsync(assert, ct);

            default:
                return (false, $"Unknown step type: {step.Type}");
        }
    }

    private async Task<(bool, string?)> EvaluateAssertAsync(AssertStep assert, CancellationToken ct)
    {
        var agv = ResolveAgv(assert.AgvId);
        if (agv == null) return (false, $"AGV '{assert.AgvId}' not found");

        Emit($"  Assert [{assert.AgvId}]: {assert.Condition} (timeout: {assert.TimeoutMs}ms)");

        var deadline = DateTime.UtcNow.AddMilliseconds(assert.TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) break;

            bool result = EvaluateCondition(agv, assert.Condition);
            if (result)
                return (true, $"Condition '{assert.Condition}' satisfied");

            await Task.Delay(200, ct);
        }

        bool finalResult = EvaluateCondition(agv, assert.Condition);
        if (finalResult)
            return (true, $"Condition '{assert.Condition}' satisfied at deadline");

        return (false, $"Condition '{assert.Condition}' NOT satisfied after {assert.TimeoutMs}ms");
    }

    /// <summary>
    /// Evaluate simple condition strings.
    /// Supported: "battery < 20", "battery > 80", "driving == true", "isConnected == true",
    ///            "errorCount == 0", "errorCount > 0", "paused == true"
    /// </summary>
    private bool EvaluateCondition(IVirtualAgv agv, string condition)
    {
        try
        {
            // Parse: "property operator value"
            var match = Regex.Match(condition.Trim(), @"^(\w+)\s*(==|!=|<|<=|>|>=)\s*(.+)$");
            if (!match.Success) return false;

            string property = match.Groups[1].Value.ToLower();
            string op = match.Groups[2].Value;
            string valueStr = match.Groups[3].Value.Trim();

            double? actualNumeric = null;
            bool? actualBool = null;

            switch (property)
            {
                case "battery":
                    actualNumeric = agv.CurrentState.BatteryState?.BatteryCharge ?? 0;
                    break;
                case "driving":
                    actualBool = agv.CurrentState.Driving;
                    break;
                case "paused":
                    actualBool = agv.CurrentState.Paused;
                    break;
                case "isconnected":
                    actualBool = agv.IsConnected;
                    break;
                case "errorcount":
                    actualNumeric = agv.GetErrors().Count;
                    break;
                case "isrunning":
                    actualBool = agv.IsRunning;
                    break;
                case "ischarging":
                    actualBool = agv.CurrentState.BatteryState?.Charging ?? false;
                    break;
                case "haserrors":
                    actualBool = agv.HasErrors;
                    break;
                case "hasfatalerrors":
                    actualBool = agv.HasFatalErrors;
                    break;
            }

            if (actualBool.HasValue)
            {
                bool expectedBool = bool.Parse(valueStr.ToLower());
                return op switch
                {
                    "==" => actualBool.Value == expectedBool,
                    "!=" => actualBool.Value != expectedBool,
                    _ => false
                };
            }

            if (actualNumeric.HasValue && double.TryParse(valueStr, out double expectedNum))
            {
                return op switch
                {
                    "==" => Math.Abs(actualNumeric.Value - expectedNum) < 0.01,
                    "!=" => Math.Abs(actualNumeric.Value - expectedNum) >= 0.01,
                    "<"  => actualNumeric.Value < expectedNum,
                    "<=" => actualNumeric.Value <= expectedNum,
                    ">"  => actualNumeric.Value > expectedNum,
                    ">=" => actualNumeric.Value >= expectedNum,
                    _ => false
                };
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private IVirtualAgv? ResolveAgv(string agvId) => _agvResolver(agvId);

    private void Emit(string message)
    {
        _logger.LogInformation("[Scenario] {Message}", message);
        LogMessage?.Invoke(this, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
