using ACS.Simulator.API.Hubs;
using ACS.Simulator.API.Models;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace ACS.Simulator.API.Services;

/// <summary>
/// Broadcasts fleet state updates to SignalR clients with adaptive interval.
/// Fast (150ms) when AGVs are moving, slow (3s) when idle.
/// </summary>
public class SimulatorBroadcastService : BackgroundService
{
    private readonly IHubContext<SimulatorHub> _hub;
    private readonly SimulatorService _simulator;
    private readonly ILogger<SimulatorBroadcastService> _logger;
    // ConcurrentQueue — lock-free, safe to enqueue from any thread
    private readonly ConcurrentQueue<FleetEventDto> _pendingEvents = new();

    // Adaptive interval: fast when AGVs are moving, slow when idle
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromMilliseconds(3000);
    private const double PositionChangeTolerance = 0.001; // meters

    // Track last known positions for change detection
    private readonly Dictionary<string, (double X, double Y, double Theta)> _lastPositions = new();

    public SimulatorBroadcastService(
        IHubContext<SimulatorHub> hub,
        SimulatorService simulator,
        ILogger<SimulatorBroadcastService> logger)
    {
        _hub = hub;
        _simulator = simulator;
        _logger = logger;

        _simulator.FleetEvent += OnFleetEvent;
    }

    private void OnFleetEvent(FleetEventDto evt)
    {
        // Lock-free enqueue — safe to call from any thread
        _pendingEvents.Enqueue(evt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[SimulatorBroadcast] Service starting with adaptive interval ({Fast}ms / {Slow}ms)",
            FastInterval.TotalMilliseconds, SlowInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool hasChanges = false;
            try
            {
                // Broadcast full fleet state
                var fleet = _simulator.GetFleetSummary();
                await _hub.Clients.Group("fleet")
                    .SendAsync("AgvFleetUpdated", fleet, stoppingToken);

                // Detect position changes for adaptive interval
                hasChanges = DetectPositionChanges(fleet);

                // Drain pending events (lock-free)
                bool hasEvents = !_pendingEvents.IsEmpty;
                while (_pendingEvents.TryDequeue(out var evt))
                {
                    hasChanges = true;
                    await _hub.Clients.Group("fleet")
                        .SendAsync("FleetEvent", evt, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SimulatorBroadcast] Broadcast error");
            }

            var interval = hasChanges ? FastInterval : SlowInterval;
            await Task.Delay(interval, stoppingToken);
        }

        _logger.LogInformation("[SimulatorBroadcast] Service stopped");
    }

    /// <summary>
    /// Compares current AGV positions with last known positions.
    /// Returns true if any AGV has moved beyond the tolerance threshold.
    /// </summary>
    private bool DetectPositionChanges(List<AgvSummaryDto> fleet)
    {
        bool hasChanges = false;
        foreach (var agv in fleet)
        {
            var id = agv.Id;
            if (_lastPositions.TryGetValue(id, out var last))
            {
                if (Math.Abs(agv.PosX - last.X) > PositionChangeTolerance ||
                    Math.Abs(agv.PosY - last.Y) > PositionChangeTolerance ||
                    Math.Abs(agv.PosTheta - last.Theta) > PositionChangeTolerance)
                {
                    hasChanges = true;
                }
            }
            else
            {
                hasChanges = true; // New AGV
            }
            _lastPositions[id] = (agv.PosX, agv.PosY, agv.PosTheta);
        }
        return hasChanges;
    }
}
