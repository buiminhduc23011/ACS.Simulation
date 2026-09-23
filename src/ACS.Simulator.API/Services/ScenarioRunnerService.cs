using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Core.Services;
using ACS.Simulator.API.Hubs;
using ACS.Simulator.API.Models;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace ACS.Simulator.API.Services;

public class ScenarioRunnerService : IAsyncDisposable
{
    private readonly SimulatorService _simulator;
    private readonly IHubContext<SimulatorHub> _hub;
    private readonly ILogger<ScenarioRunnerService> _logger;

    private readonly Dictionary<string, ScenarioDefinition> _scenarios = new();
    private ScenarioRunDto? _currentRun;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly string _scenariosDir;

    public ScenarioRunnerService(
        SimulatorService simulator,
        IHubContext<SimulatorHub> hub,
        ILogger<ScenarioRunnerService> logger,
        IWebHostEnvironment env)
    {
        _simulator = simulator;
        _hub = hub;
        _logger = logger;
        _scenariosDir = Path.Combine(env.ContentRootPath, "Scenarios");
        Directory.CreateDirectory(_scenariosDir);
        LoadFromDisk();
    }

    private void LoadFromDisk()
    {
        foreach (var file in Directory.GetFiles(_scenariosDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var scenario = JsonSerializer.Deserialize<Scenario>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (scenario != null)
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    _scenarios[id] = new ScenarioDefinition { Id = id, Scenario = scenario, FilePath = file };
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Failed to load scenario {File}", file); }
        }
    }

    public List<ScenarioSummaryDto> GetAll()
    {
        return _scenarios.Values.Select(s => new ScenarioSummaryDto(
            s.Id, s.Scenario.Name, s.Scenario.Description, s.Scenario.Version,
            s.Scenario.Steps.Count, s.FilePath)).ToList();
    }

    public async Task<string> ImportAsync(string fileName, string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var scenario = JsonSerializer.Deserialize<Scenario>(json, options)
            ?? throw new InvalidOperationException("Invalid scenario JSON");

        var id = Path.GetFileNameWithoutExtension(fileName);
        var path = Path.Combine(_scenariosDir, $"{id}.json");
        await File.WriteAllTextAsync(path, json);
        _scenarios[id] = new ScenarioDefinition { Id = id, Scenario = scenario, FilePath = path };
        return id;
    }

    public string GetJson(string id)
    {
        if (!_scenarios.TryGetValue(id, out var def)) throw new KeyNotFoundException(id);
        return File.ReadAllText(def.FilePath);
    }

    public ScenarioRunDto? GetCurrentRun() => _currentRun;

    public async Task<ScenarioRunDto> RunAsync(string scenarioId, string? defaultAgvId)
    {
        if (!_scenarios.TryGetValue(scenarioId, out var def))
            throw new KeyNotFoundException($"Scenario '{scenarioId}' not found");

        await StopCurrentRunAsync();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var runId = Guid.NewGuid().ToString("N")[..8];
        _currentRun = new ScenarioRunDto(
            runId, def.Scenario.Name, "Running",
            def.Scenario.Steps.Count, 0, 0,
            DateTime.UtcNow, null, new List<StepResultDto>());

        await BroadcastProgress("ScenarioStarted", _currentRun);

        _runTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteScenarioAsync(def.Scenario, defaultAgvId, runId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal stop/dispose path.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scenario runner task failed for run {RunId}", runId);
            }
        });

        return _currentRun;
    }

    private async Task ExecuteScenarioAsync(Scenario scenario, string? defaultAgvId, string runId, CancellationToken ct)
    {
        var results = new List<StepResultDto>();
        int passed = 0, failed = 0;

        for (int i = 0; i < scenario.Steps.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var step = scenario.Steps[i];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = false;
            string? msg = null;

            await LogStep(i, step, "Running", null, 0);

            try
            {
                (ok, msg) = await ExecuteStepAsync(step, defaultAgvId, ct);
            }
            catch (Exception ex) { ok = false; msg = ex.Message; }

            sw.Stop();
            if (ok) passed++; else failed++;

            var result = new StepResultDto(i, step.Type,
                GetAgvIdFromStep(step, defaultAgvId),
                step.Comment, ok, msg, sw.ElapsedMilliseconds);
            results.Add(result);

            _currentRun = _currentRun! with
            {
                PassedSteps = passed,
                FailedSteps = failed,
                StepResults = new List<StepResultDto>(results)
            };

            await BroadcastProgress("ScenarioStep", result);
            await BroadcastProgress("ScenarioProgress", _currentRun);
        }

        var finalStatus = ct.IsCancellationRequested ? "Stopped"
            : failed == 0 ? "Passed" : "Failed";

        _currentRun = _currentRun! with
        {
            Status = finalStatus,
            FinishedAt = DateTime.UtcNow,
            StepResults = new List<StepResultDto>(results)
        };

        await BroadcastProgress("ScenarioFinished", _currentRun);
    }

    private string ResolveAgvId(string? stepAgvId, string? defaultAgvId)
    {
        if (!string.IsNullOrEmpty(stepAgvId)) return stepAgvId;
        if (!string.IsNullOrEmpty(defaultAgvId)) return defaultAgvId;
        var first = _simulator.GetAll().FirstOrDefault();
        return first?.SerialNumber ?? "";
    }

    private async Task<(bool ok, string? msg)> ExecuteStepAsync(
        ScenarioStep step, string? defaultAgvId, CancellationToken ct)
    {
        switch (step)
        {
            case WaitStep w:
                await Task.Delay(w.DurationMs, ct);
                return (true, $"Waited {w.DurationMs}ms");

            case InjectErrorStep ie:
                var ieId = ResolveAgvId(ie.AgvId, defaultAgvId);
                var injected = await _simulator.InjectTemplateAsync(ieId, ie.TemplateName);
                return (injected, injected ? null : $"AGV {ieId} not found or template unknown");

            case ClearErrorStep ce:
                var ceId = ResolveAgvId(ce.AgvId, defaultAgvId);
                var agv = _simulator.Get(ceId);
                if (agv == null) return (false, $"AGV {ceId} not found");
                if (ce.ErrorType == null) await agv.ClearAllErrorsAsync();
                else await agv.ClearErrorAsync(ce.ErrorType);
                return (true, null);

            case DisconnectStep d:
                var dId = ResolveAgvId(d.AgvId, defaultAgvId);
                var ok = await _simulator.TriggerDisconnectAsync(dId, d.DurationMs);
                return (ok, ok ? null : $"AGV {dId} not found");

            case SetBatteryStep sb:
                var sbId = ResolveAgvId(sb.AgvId, defaultAgvId);
                return (_simulator.SetBattery(sbId, sb.Level), null);

            case SetPositionStep sp:
                var spId = ResolveAgvId(sp.AgvId, defaultAgvId);
                return (_simulator.SetPosition(spId, new Models.SetPositionRequest(sp.X, sp.Y, sp.Theta, sp.MapId)), null);

            case SetSpeedStep ss:
                var ssId = ResolveAgvId(ss.AgvId, defaultAgvId);
                return (_simulator.SetSpeed(ssId, ss.Speed), null);

            case AssertStep a:
                return await EvaluateAssertAsync(a, defaultAgvId, ct);

            case LogStep l:
                var logMsg = new LogMessageDto("SCENARIO", "INFO", l.Message, DateTime.UtcNow);
                await BroadcastProgress("LogMessage", logMsg);
                return (true, null);

            case SetChaosStep sc:
                var scId = ResolveAgvId(sc.AgvId, defaultAgvId);
                var chaosReq = new ChaosSettingsRequest(
                    sc.LatencyMinMs > 0, sc.LatencyMinMs, sc.LatencyMaxMs,
                    sc.PacketLossPercent > 0, sc.PacketLossPercent);
                return (_simulator.SetChaos(scId, chaosReq), null);

            default:
                return (false, $"Unknown step type: {step.Type}");
        }
    }

    private async Task<(bool, string?)> EvaluateAssertAsync(
        AssertStep a, string? defaultAgvId, CancellationToken ct)
    {
        var agvId = ResolveAgvId(a.AgvId, defaultAgvId);
        var agv = _simulator.Get(agvId);
        if (agv == null) return (false, $"AGV {agvId} not found");

        var deadline = DateTime.UtcNow.AddMilliseconds(a.TimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return (false, "Cancelled");
            var state = agv.CurrentState;
            bool result = a.Condition.ToLower() switch
            {
                string c when c.Contains("battery") && c.Contains("<") => state.BatteryState.BatteryCharge < ParseNum(c),
                string c when c.Contains("battery") && c.Contains(">") => state.BatteryState.BatteryCharge > ParseNum(c),
                "driving == true" or "isdriving" => state.Driving,
                "driving == false" => !state.Driving,
                "isconnected == true" or "isconnected" => agv.IsConnected,
                "isconnected == false" => !agv.IsConnected,
                string c when c.Contains("errorcount >") => agv.GetErrors().Count > (int)ParseNum(c),
                "haserrors" or "haserrors == true" => agv.HasErrors,
                "hasfatalerrors" or "hasfatalerrors == true" => agv.HasFatalErrors,
                _ => false
            };
            if (result) return (true, $"Assert passed: {a.Condition}");
            await Task.Delay(200, ct);
        }
        return (false, $"Assert timeout: {a.Condition}");
    }

    private static double ParseNum(string condition)
    {
        var parts = condition.Split(new[] { '<', '>', '=' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && double.TryParse(parts[^1].Trim(), out var n) ? n : 0;
    }

    private string? GetAgvIdFromStep(ScenarioStep step, string? defaultId) => step switch
    {
        InjectErrorStep ie => ResolveAgvId(ie.AgvId, defaultId),
        ClearErrorStep ce => ResolveAgvId(ce.AgvId, defaultId),
        DisconnectStep d => ResolveAgvId(d.AgvId, defaultId),
        SetBatteryStep sb => ResolveAgvId(sb.AgvId, defaultId),
        SetPositionStep sp => ResolveAgvId(sp.AgvId, defaultId),
        SetSpeedStep ss => ResolveAgvId(ss.AgvId, defaultId),
        AssertStep a => ResolveAgvId(a.AgvId, defaultId),
        SetChaosStep sc => ResolveAgvId(sc.AgvId, defaultId),
        _ => defaultId
    };

    private async Task LogStep(int index, ScenarioStep step, string status, string? msg, long ms)
    {
        var log = new LogMessageDto("SCENARIO", "DEBUG", $"Step {index}: {step.Type} — {status}", DateTime.UtcNow);
        await BroadcastProgress("LogMessage", log);
    }

    private async Task BroadcastProgress(string eventName, object payload)
    {
        await _hub.Clients.Group("scenario").SendAsync(eventName, payload);
    }

    public void Stop() => _runCts?.Cancel();

    public async ValueTask DisposeAsync()
    {
        await StopCurrentRunAsync();
        _runCts?.Dispose();
        _runCts = null;
        GC.SuppressFinalize(this);
    }

    private async Task StopCurrentRunAsync()
    {
        var task = _runTask;
        _runCts?.Cancel();

        if (task is { IsCompleted: false })
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Scenario runner task did not stop within timeout");
            }
            catch (OperationCanceledException)
            {
            }
        }

        _runTask = null;
        _runCts?.Dispose();
        _runCts = null;
    }
}

public class ScenarioDefinition
{
    public string Id { get; set; } = "";
    public Scenario Scenario { get; set; } = new();
    public string FilePath { get; set; } = "";
}
