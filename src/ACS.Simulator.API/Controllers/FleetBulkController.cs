using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

/// <summary>
/// Bulk fleet operations — create/start/stop/delete multiple AGVs in one request.
/// Designed for 30+ AGV scenarios where calling per-AGV APIs individually is impractical.
/// </summary>
[ApiController]
[Route("api/fleet/bulk")]
public class FleetBulkController : ControllerBase
{
    private readonly SimulatorService _simulator;
    private readonly ILogger<FleetBulkController> _logger;

    public FleetBulkController(SimulatorService simulator, ILogger<FleetBulkController> logger)
    {
        _simulator = simulator;
        _logger = logger;
    }

    /// <summary>
    /// Create N AGVs in a grid layout.
    /// POST /api/fleet/bulk-create
    /// </summary>
    [HttpPost("create")]
    public async Task<IActionResult> BulkCreate([FromBody] BulkCreateRequest req)
    {
        if (req.Count <= 0 || req.Count > 100)
            return BadRequest("Count must be between 1 and 100.");

        int cols = req.Columns <= 0 ? (int)Math.Ceiling(Math.Sqrt(req.Count)) : req.Columns;
        double spacing = req.Spacing <= 0 ? 3.0 : req.Spacing;

        var created = new List<string>();
        var failed = new List<string>();

        for (int i = 0; i < req.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            var padWidth = req.SerialNumberPadWidth is >= 1 and <= 6 ? req.SerialNumberPadWidth : 3;
            var serial = $"{req.SerialPrefix}{(i + req.StartIndex).ToString($"D{padWidth}")}";

            var createReq = new CreateAgvRequest(
                SerialNumber: serial,
                Manufacturer: req.Manufacturer ?? "stivn",
                IpAddress: null,
                MacAddress: null,
                PosX: req.OriginX + col * spacing,
                PosY: req.OriginY + row * spacing,
                PosTheta: req.Theta,
                MapId: req.MapId ?? ""
            );

            try
            {
                var result = await _simulator.CreateAgvAsync(createReq);
                if (result != null) created.Add(serial);
                else failed.Add($"{serial} (duplicate)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BulkCreate: failed to create {Serial}", serial);
                failed.Add($"{serial} ({ex.Message})");
            }
        }

        return Ok(new { Created = created.Count, Failed = failed.Count, Serials = created, Errors = failed });
    }

    /// <summary>
    /// Start (connect + start) all AGVs in the fleet in parallel.
    /// POST /api/fleet/bulk-start
    /// </summary>
    [HttpPost("start")]
    public async Task<IActionResult> BulkStart()
    {
        var all = _simulator.GetAll().Select(a => a.SerialNumber).ToList();
        _logger.LogInformation("BulkStart: starting {Count} AGVs in parallel", all.Count);

        var results = await Task.WhenAll(all.Select(async id =>
        {
            try { return await _simulator.StartAgvAsync(id) ? id : null; }
            catch { return null; }
        }));

        var started = results.Where(r => r != null).ToList();
        return Ok(new { Started = started.Count, Total = all.Count });
    }

    /// <summary>
    /// Stop all AGVs in the fleet in parallel.
    /// POST /api/fleet/bulk-stop
    /// </summary>
    [HttpPost("stop")]
    public async Task<IActionResult> BulkStop()
    {
        var all = _simulator.GetAll().Select(a => a.SerialNumber).ToList();
        _logger.LogInformation("BulkStop: stopping {Count} AGVs in parallel", all.Count);

        await Task.WhenAll(all.Select(id => _simulator.StopAgvAsync(id)));
        return Ok(new { Stopped = all.Count });
    }

    /// <summary>
    /// Delete all AGVs in the fleet.
    /// POST /api/fleet/bulk-delete
    /// </summary>
    [HttpPost("delete")]
    public async Task<IActionResult> BulkDelete()
    {
        var all = _simulator.GetAll().Select(a => a.SerialNumber).ToList();
        _logger.LogWarning("BulkDelete: deleting {Count} AGVs", all.Count);

        await Task.WhenAll(all.Select(id => _simulator.DeleteAgvAsync(id)));
        return Ok(new { Deleted = all.Count });
    }
}

// ─── DTOs ───────────────────────────────────────────────────────────────────

public record BulkCreateRequest(
    int Count,
    string SerialPrefix,
    int StartIndex = 1,
    string? Manufacturer = "stivn",
    string? MapId = "",
    double OriginX = 0,
    double OriginY = 0,
    double Theta = 0,
    double Spacing = 3.0,
    int Columns = 0,   // 0 = auto (sqrt)
    int SerialNumberPadWidth = 3 // default 3 => AGV-001 style serials (scenario-friendly)
);
