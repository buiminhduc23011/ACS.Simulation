using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Core.Services;
using ACS.Simulator.API.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ACS.Simulator.API.Services;

/// <summary>
/// Core singleton service — manages the fleet of VirtualAgv instances.
/// </summary>
public class SimulatorService
{
    private const double MinSeparationMeters = 1.50;
    private const double FrontStopDistanceMeters = 2.50;
    private const double FrontConeHalfAngleRad = Math.PI / 3.0; // 60 degrees
    private const double RotateFrontConeHalfAngleRad = Math.PI / 4.0; // 45 degrees

    private readonly ILogger<SimulatorService> _logger;
    private readonly SimulatorConfig _config;
    private readonly FleetPersistenceService _persistence;
    // ConcurrentDictionary — safe for concurrent reads (FindBlockingAgv) without locking
    private readonly ConcurrentDictionary<string, IVirtualAgv> _fleet = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgvMeta> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mapGraphLock = new();
    private readonly Dictionary<string, SimMapGraph> _knownMapGraphs = new(StringComparer.OrdinalIgnoreCase);
    private SimMapGraph? _primaryMapGraph;
    // _lock guards only write operations (Create/Update/Delete) atomically
    private readonly SemaphoreSlim _lock = new(1, 1);
    private System.Threading.Timer? _saveDebounceTimer;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(1500);

    public event Action<string, string>? AgvStatusChanged; // (agvId, status)
    public event Action<FleetEventDto>? FleetEvent;

    public SimulatorService(ILogger<SimulatorService> logger, SimulatorConfig config, FleetPersistenceService persistence)
    {
        _logger = logger;
        _config = config;
        _persistence = persistence;
    }

    public IEnumerable<IVirtualAgv> GetAll() => _fleet.Values;

    public IVirtualAgv? Get(string id) => _fleet.TryGetValue(id, out var agv) ? agv : null;

    public AgvMeta? GetMeta(string id) => _meta.TryGetValue(id, out var m) ? m : null;

    public async Task<AgvSummaryDto?> CreateAgvAsync(CreateAgvRequest req)
    {
        await _lock.WaitAsync();
        try
        {
            var id = req.SerialNumber;
            if (_fleet.ContainsKey(id))
                return null;

            var agvConfig = new AgvConfiguration
            {
                SerialNumber = req.SerialNumber,
                Manufacturer = req.Manufacturer,
                IpAddress = req.IpAddress ?? "192.168.1.100",
                MacAddress = req.MacAddress ?? GenerateMac(req.SerialNumber),
                VehicleShapeId = req.VehicleShapeId,
                NavigationTechnologyId = req.NavigationTechnologyId,
                MapMappings = NormalizeMapMappings(req.MapMappings),
                InitialPosition = new PositionInfo
                {
                    X = req.PosX,
                    Y = req.PosY,
                    Theta = req.PosTheta,
                    MapId = req.MapId
                }
            };

            var mqttConfig = new MqttBrokerConfig
            {
                Address = _config.MqttHost,
                Port = _config.MqttPort
            };

            using var logFactory = LoggerFactory.Create(b => b.AddConsole());
            var agvLogger = logFactory.CreateLogger<VirtualAgv>();
            var agv = new VirtualAgv(
                agvConfig,
                mqttConfig,
                new MovementConfig(),
                new StatePublishConfig(),
                new ActionsConfig(),
                agvLogger);
            agv.SetMovementBlockResolver(FindBlockingAgv);
            ApplyKnownMapGraphs(agv);

            _fleet[id] = agv;
            _meta[id] = new AgvMeta
            {
                Id = id,
                Manufacturer = req.Manufacturer,
                Speed = 1.0,
                MapMappings = agvConfig.MapMappings
                    .Select(mapping => new AgvMapMapping
                    {
                        SourceMapId = mapping.SourceMapId,
                        TargetMapId = mapping.TargetMapId
                    })
                    .ToList()
            };

            _logger.LogInformation("AGV created: {SerialNumber}", id);
            EmitEvent(id, "Created", $"AGV {id} added to fleet");

            _ = SaveFleetAsync(); // fire-and-forget persist

            return BuildSummary(id, agv);
        }
        finally { _lock.Release(); }
    }

    public async Task<AgvSummaryDto?> UpdateAgvAsync(string id, UpdateAgvRequest req)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_fleet.TryGetValue(id, out var oldAgv)) return null;

            // Stop and dispose the old AGV
            await oldAgv.StopAsync();
            oldAgv.Dispose();
            _fleet.TryRemove(id, out _);
            _meta.TryRemove(id, out _);

            // Create new AGV with updated config
            var newId = req.SerialNumber;
            var agvConfig = new AgvConfiguration
            {
                SerialNumber = req.SerialNumber,
                Manufacturer = req.Manufacturer,
                IpAddress = req.IpAddress ?? "192.168.1.100",
                MacAddress = req.MacAddress ?? GenerateMac(req.SerialNumber),
                VehicleShapeId = req.VehicleShapeId,
                NavigationTechnologyId = req.NavigationTechnologyId,
                MapMappings = NormalizeMapMappings(req.MapMappings),
                InitialPosition = new PositionInfo
                {
                    X = req.PosX,
                    Y = req.PosY,
                    Theta = req.PosTheta,
                    MapId = req.MapId
                }
            };

            var mqttConfig = new MqttBrokerConfig
            {
                Address = _config.MqttHost,
                Port = _config.MqttPort
            };

            using var logFactory = LoggerFactory.Create(b => b.AddConsole());
            var agvLogger = logFactory.CreateLogger<VirtualAgv>();
            var newAgv = new VirtualAgv(
                agvConfig,
                mqttConfig,
                new MovementConfig(),
                new StatePublishConfig(),
                new ActionsConfig(),
                agvLogger);
            newAgv.SetMovementBlockResolver(FindBlockingAgv);
            ApplyKnownMapGraphs(newAgv);

            _fleet[newId] = newAgv;
            _meta[newId] = new AgvMeta
            {
                Id = newId,
                Manufacturer = req.Manufacturer,
                Speed = 1.0,
                MapMappings = agvConfig.MapMappings
                    .Select(mapping => new AgvMapMapping
                    {
                        SourceMapId = mapping.SourceMapId,
                        TargetMapId = mapping.TargetMapId
                    })
                    .ToList()
            };

            _logger.LogInformation("AGV updated: {OldId} -> {NewId}", id, newId);
            EmitEvent(newId, "Updated", $"AGV {newId} updated");

            _ = SaveFleetAsync(); // fire-and-forget persist

            return BuildSummary(newId, newAgv);
        }
        finally { _lock.Release(); }
    }

    public async Task<bool> StartAgvAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        await agv.ConnectAsync();
        await agv.StartAsync();
        _ = SaveFleetAsync(); // fire-and-forget persist
        EmitEvent(id, "Started", $"AGV {id} started");
        return true;
    }

    public async Task<bool> StopAgvAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        await agv.StopAsync();
        _ = SaveFleetAsync(); // fire-and-forget persist
        EmitEvent(id, "Stopped", $"AGV {id} stopped");
        return true;
    }

    public async Task<bool> DeleteAgvAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            if (!_fleet.TryGetValue(id, out var agv)) return false;
            await agv.StopAsync();
            agv.Dispose();
            _fleet.TryRemove(id, out _);
            _meta.TryRemove(id, out _);
            EmitEvent(id, "Deleted", $"AGV {id} removed from fleet");

            _ = SaveFleetAsync(); // fire-and-forget persist

            return true;
        }
        finally { _lock.Release(); }
    }

    public bool SetPosition(string id, SetPositionRequest req)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        var effectiveMapId = string.IsNullOrEmpty(req.MapId)
            ? agv.CurrentState.AgvPosition.MapId ?? ""
            : req.MapId;
        agv.SetPosition(req.X, req.Y, req.Theta, effectiveMapId);
        // Thread-safe debounce: atomically swap the timer reference
        var newTimer = new System.Threading.Timer(_ => { _ = SaveFleetAsync(); }, null, SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        var oldTimer = Interlocked.Exchange(ref _saveDebounceTimer, newTimer);
        oldTimer?.Dispose();
        return true;
    }

    public bool SetBattery(string id, double level)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        agv.SetBatteryLevel(level);
        return true;
    }

    public bool SetSpeed(string id, double speed)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        agv.SetSpeed(speed);
        if (_meta.TryGetValue(id, out var m)) m.Speed = speed;
        return true;
    }

    public bool SetOperatingMode(string id, string mode)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        agv.SetOperatingMode(mode);
        EmitEvent(id, "OperatingModeChanged", $"AGV {id} operatingMode → {mode}");
        return true;
    }

    public bool ClearOrderState(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        agv.ClearOrderState();
        EmitEvent(id, "OrderCleared", $"AGV {id} cleared order state");
        return true;
    }

    public List<InboundMqttMessageDto>? GetInboundMessages(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return null;
        return agv.GetRecentInboundMessages()
            .Select(m => new InboundMqttMessageDto(
                m.Timestamp,
                m.TopicType,
                m.Topic,
                m.Payload,
                m.Accepted,
                m.Note))
            .ToList();
    }

    public async Task<bool> LiftAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        await agv.LiftAsync();
        EmitEvent(id, "Lift", $"AGV {id} lifted load");
        return true;
    }

    public async Task<bool> LowerAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        await agv.LowerAsync();
        EmitEvent(id, "Lower", $"AGV {id} lowered load");
        return true;
    }

    public async Task<int> ClearLoadsAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return -1;
        var cleared = await agv.ClearLoadsAsync();
        EmitEvent(id, "LoadsCleared", $"AGV {id} cleared load state");
        return cleared;
    }

    public async Task<bool> AddErrorAsync(string id, AddErrorRequest req)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        var description = req.Description ?? req.ErrorDescription;
        await agv.AddErrorAsync(req.ErrorType, req.ErrorLevel, description);
        EmitEvent(id, "ErrorAdded", $"Error {req.ErrorType} added to AGV {id}");
        return true;
    }

    public async Task<int> ClearAllErrorsAsync(string id)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return -1;
        var cleared = await agv.ClearAllErrorsAsync();
        EmitEvent(id, "ErrorsCleared", $"All errors cleared on AGV {id}");
        return cleared;
    }

    public async Task<bool> InjectTemplateAsync(string id, string templateName)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        var template = ErrorTemplates.GetByName(templateName);
        if (template == null) return false;
        await agv.InjectErrorTemplateAsync(template);
        EmitEvent(id, "ErrorInjected", $"Template {templateName} injected on AGV {id}");
        return true;
    }

    public bool SetChaos(string id, ChaosSettingsRequest req)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        agv.SetChaosLatency(req.LatencyEnabled ? req.LatencyMinMs : 0,
                            req.LatencyEnabled ? req.LatencyMaxMs : 0);
        agv.SetPacketLoss(req.PacketLossEnabled ? req.PacketLossPercent : 0);
        if (_meta.TryGetValue(id, out var m))
        {
            m.ChaosLatencyEnabled = req.LatencyEnabled;
            m.ChaosLatencyMin = req.LatencyMinMs;
            m.ChaosLatencyMax = req.LatencyMaxMs;
            m.ChaosPacketLossEnabled = req.PacketLossEnabled;
            m.ChaosPacketLoss = req.PacketLossPercent;
        }
        return true;
    }

    public async Task<bool> TriggerDisconnectAsync(string id, int durationMs)
    {
        if (!_fleet.TryGetValue(id, out var agv)) return false;
        await agv.TriggerDisconnectAsync(durationMs);
        EmitEvent(id, "Disconnected", $"AGV {id} temporarily disconnected ({durationMs}ms)");
        return true;
    }

    public void SetMapGraph(SimMapGraph? graph)
    {
        lock (_mapGraphLock)
        {
            _knownMapGraphs.Clear();
            _primaryMapGraph = graph;
            if (graph != null && !string.IsNullOrWhiteSpace(graph.MapId))
            {
                _knownMapGraphs[graph.MapId] = graph;
            }
        }

        foreach (var agv in _fleet.Values)
            agv.SetMapGraph(graph);

        _logger.LogInformation(
            "Simulator primary map graph {Action}: MapId={MapId}, Nodes={NodeCount}, Edges={EdgeCount}",
            graph == null ? "cleared" : "set",
            graph?.MapId ?? "",
            graph?.Nodes.Count ?? 0,
            graph?.Adjacency.Values.Sum(edges => edges.Count) ?? 0);
    }

    public void AddMapGraph(SimMapGraph graph)
    {
        if (graph == null || string.IsNullOrWhiteSpace(graph.MapId))
        {
            return;
        }

        lock (_mapGraphLock)
        {
            _knownMapGraphs[graph.MapId] = graph;
            _primaryMapGraph ??= graph;
        }

        foreach (var agv in _fleet.Values)
            agv.AddMapGraph(graph);

        _logger.LogInformation(
            "Simulator map graph loaded: MapId={MapId}, Nodes={NodeCount}, Edges={EdgeCount}",
            graph.MapId,
            graph.Nodes.Count,
            graph.Adjacency.Values.Sum(edges => edges.Count));
    }

    public List<AgvSummaryDto> GetFleetSummary()
    {
        return _fleet.Select(kv => BuildSummary(kv.Key, kv.Value)).ToList();
    }

    private AgvSummaryDto BuildSummary(string id, IVirtualAgv agv)
    {
        var state = agv.CurrentState;
        var pos = state.AgvPosition;
        var vel = state.Velocity;
        _meta.TryGetValue(id, out var meta);
        var status = !agv.IsConnected ? "OFFLINE"
            : agv.HasFatalErrors ? "ERROR"
            : state.Driving ? "DRIVING"
            : "IDLE";

        return new AgvSummaryDto(
            Id: id,
            SerialNumber: agv.SerialNumber,
            Manufacturer: meta?.Manufacturer ?? "",
            IpAddress: agv.IpAddress,
            MacAddress: agv.MacAddress,
            Status: status,
            IsConnected: agv.IsConnected,
            IsRunning: agv.IsRunning,
            PosX: pos.X,
            PosY: pos.Y,
            PosTheta: pos.Theta,
            MapId: pos.MapId ?? "",
            BatteryLevel: state.BatteryState.BatteryCharge,
            IsCharging: state.BatteryState.Charging,
            HasErrors: agv.HasErrors,
            ErrorCount: agv.GetErrors().Count,
            SpeedX: vel.Vx,
            SpeedY: vel.Vy,
            LoadCount: state.Loads.Count,
            OperatingMode: state.OperatingMode,
            OrderId: state.OrderId ?? "",
            OrderUpdateId: state.OrderUpdateId,
            MapMappings: meta?.MapMappings
                .Select(mapping => new AgvMapMappingDto(mapping.SourceMapId, mapping.TargetMapId))
                .ToList()
        );
    }

    private void EmitEvent(string agvId, string type, string message)
    {
        FleetEvent?.Invoke(new FleetEventDto(agvId, type, message, DateTime.UtcNow));
    }

    private void ApplyKnownMapGraphs(IVirtualAgv agv)
    {
        SimMapGraph? primary;
        List<SimMapGraph> additional;
        lock (_mapGraphLock)
        {
            primary = _primaryMapGraph;
            additional = _knownMapGraphs.Values
                .Where(graph => primary == null ||
                    !string.Equals(graph.MapId, primary.MapId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (primary != null)
        {
            agv.SetMapGraph(primary);
        }

        foreach (var graph in additional)
        {
            agv.AddMapGraph(graph);
        }
    }

    private string? FindBlockingAgv(
        string selfId,
        double currentX, double currentY,
        double nextX, double nextY,
        double headingRad, bool isRotating, string mapId)
    {
        // ConcurrentDictionary — safe to enumerate without locking
        var moveDx = nextX - currentX;
        var moveDy = nextY - currentY;
        var moveLength = Math.Sqrt(moveDx * moveDx + moveDy * moveDy);
        var dirX = moveLength > 1e-6 ? moveDx / moveLength : Math.Cos(headingRad);
        var dirY = moveLength > 1e-6 ? moveDy / moveLength : Math.Sin(headingRad);
        var frontCosThreshold = Math.Cos(FrontConeHalfAngleRad);
        var rotateFrontCosThreshold = Math.Cos(RotateFrontConeHalfAngleRad);

        foreach (var (agvId, agv) in _fleet)
        {
            if (agvId.Equals(selfId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!agv.IsRunning) continue;

            var pos = agv.CurrentState.AgvPosition;
            if (pos == null) continue;
            var otherMap = pos.MapId ?? "";
            if (!string.IsNullOrWhiteSpace(mapId) &&
                !otherMap.Equals(mapId, StringComparison.OrdinalIgnoreCase)) continue;

            var distToNext = Math.Sqrt((pos.X - nextX) * (pos.X - nextX) + (pos.Y - nextY) * (pos.Y - nextY));
            if (distToNext < MinSeparationMeters) return agvId;

            var toOtherX = pos.X - currentX;
            var toOtherY = pos.Y - currentY;
            var distToCurrent = Math.Sqrt(toOtherX * toOtherX + toOtherY * toOtherY);
            if (distToCurrent < 1e-6) return agvId;

            var facingDot = (toOtherX / distToCurrent) * dirX + (toOtherY / distToCurrent) * dirY;
            var requiredDot = isRotating ? rotateFrontCosThreshold : frontCosThreshold;
            if (facingDot >= requiredDot && distToCurrent < FrontStopDistanceMeters) return agvId;
        }
        return null;
    }

    private static string GenerateMac(string serial)
    {
        var hash = serial.GetHashCode();
        return $"02:{(hash >> 16 & 0xFF):X2}:{(hash >> 8 & 0xFF):X2}:{(hash & 0xFF):X2}:00:01";
    }

    // ─── Persistence ───────────────────────────────────────────────

    /// <summary>
    /// Save current fleet to JSON file (fire-and-forget safe).
    /// </summary>
    private async Task SaveFleetAsync()
    {
        try
        {
            var configs = _fleet.Select(kv =>
            {
                var agv = kv.Value;
                var pos = agv.CurrentState.AgvPosition;
                _meta.TryGetValue(kv.Key, out var meta);

                return new SavedAgvConfig
                {
                    SerialNumber = kv.Key,
                    Manufacturer = meta?.Manufacturer ?? "",
                    IpAddress = agv.IpAddress,
                    MacAddress = agv.MacAddress,
                    PosX = pos.X,
                    PosY = pos.Y,
                    PosTheta = pos.Theta,
                    MapId = !string.IsNullOrEmpty(pos.MapId) ? pos.MapId : "",
                    Speed = meta?.Speed ?? 1.0,
                    AutoStart = agv.IsRunning,
                    MapMappings = meta?.MapMappings
                        .Select(mapping => new SavedAgvMapMapping
                        {
                            SourceMapId = mapping.SourceMapId,
                            TargetMapId = mapping.TargetMapId
                        })
                        .ToList() ?? new List<SavedAgvMapMapping>()
                };
            });

            await _persistence.SaveAsync(configs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save fleet config");
        }
    }

    /// <summary>
    /// Restore fleet from saved JSON file on startup.
    /// Creates AGVs and optionally auto-starts (connects + starts) them.
    /// </summary>
    public async Task RestoreFleetAsync()
    {
        var savedAgvs = await _persistence.LoadAsync();
        if (savedAgvs.Count == 0) return;

        _logger.LogInformation("[FleetRestore] Restoring {Count} AGV(s)...", savedAgvs.Count);

        foreach (var saved in savedAgvs)
        {
            try
            {
                var req = new CreateAgvRequest(
                    SerialNumber: saved.SerialNumber,
                    Manufacturer: saved.Manufacturer,
                    IpAddress: saved.IpAddress,
                    MacAddress: saved.MacAddress,
                    PosX: saved.PosX,
                    PosY: saved.PosY,
                    PosTheta: saved.PosTheta,
                    MapId: saved.MapId,
                    MapMappings: saved.MapMappings
                        .Select(mapping => new AgvMapMappingDto(mapping.SourceMapId, mapping.TargetMapId))
                        .ToList()
                );

                var result = await CreateAgvAsync(req);
                if (result == null)
                {
                    _logger.LogWarning("[FleetRestore] Failed to create AGV {Serial} (duplicate?)", saved.SerialNumber);
                    continue;
                }

                // Restore speed
                if (saved.Speed != 1.0)
                    SetSpeed(saved.SerialNumber, saved.Speed);

                // Auto-start only AGVs that were running before shutdown.
                if (saved.AutoStart)
                {
                    await StartAgvAsync(saved.SerialNumber);
                    _logger.LogInformation("[FleetRestore] AGV {Serial} restored and started", saved.SerialNumber);
                }
                else
                {
                    _logger.LogInformation("[FleetRestore] AGV {Serial} restored (not auto-started)", saved.SerialNumber);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FleetRestore] Failed to restore AGV {Serial}", saved.SerialNumber);
            }
        }

        _logger.LogInformation("[FleetRestore] Fleet restoration complete");
    }

    private static List<AgvMapMapping> NormalizeMapMappings(IEnumerable<AgvMapMappingDto>? mappings)
    {
        if (mappings == null)
        {
            return new List<AgvMapMapping>();
        }

        return mappings
            .Where(mapping =>
                !string.IsNullOrWhiteSpace(mapping.SourceMapId) &&
                !string.IsNullOrWhiteSpace(mapping.TargetMapId))
            .Select(mapping => new AgvMapMapping
            {
                SourceMapId = mapping.SourceMapId.Trim(),
                TargetMapId = mapping.TargetMapId.Trim()
            })
            .GroupBy(mapping => mapping.SourceMapId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
    }
}

public class AgvMeta
{
    public string Id { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    public List<AgvMapMapping> MapMappings { get; set; } = new();
    public bool ChaosLatencyEnabled { get; set; }
    public int ChaosLatencyMin { get; set; }
    public int ChaosLatencyMax { get; set; }
    public bool ChaosPacketLossEnabled { get; set; }
    public int ChaosPacketLoss { get; set; }
}
