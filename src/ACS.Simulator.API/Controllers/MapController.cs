using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using System.Text.Json;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/maps")]
public class MapController : ControllerBase
{
    private readonly SimulatorService _sim;
    private readonly SimulatorConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<MapController> _logger;

    public MapController(SimulatorService sim, SimulatorConfig config,
        IHttpClientFactory httpFactory, ILogger<MapController> logger)
    {
        _sim = sim;
        _config = config;
        _http = httpFactory.CreateClient("AcsApi");
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetMaps()
    {
        try
        {
            var url = $"{_config.AcsApiBaseUrl}/api/vda5050/maps";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Upstream error");
            var json = await response.Content.ReadAsStringAsync();
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy maps");
            return StatusCode(503, new { message = "Cannot connect to ACS API", detail = ex.Message });
        }
    }

    [HttpGet("{mapId}")]
    public async Task<IActionResult> GetMapDetail(string mapId)
    {
        try
        {
            var url = $"{_config.AcsApiBaseUrl}/api/vda5050/maps/{mapId}";
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return StatusCode((int)response.StatusCode, "Upstream error");
            var json = await response.Content.ReadAsStringAsync();

            // Also push the map graph to simulator service
            try
            {
                using var doc = JsonDocument.Parse(json);
                // Parse and set map graph
            }
            catch { /* swallow */ }

            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to proxy map detail {MapId}", mapId);
            return StatusCode(503, new { message = "Cannot connect to ACS API", detail = ex.Message });
        }
    }

    [HttpPost("{mapId}/activate")]
    public async Task<IActionResult> ActivateMap(string mapId)
    {
        try
        {
            var url = $"{_config.AcsApiBaseUrl}/api/vda5050/maps/{mapId}";
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            // Build SimMapGraph and push to all AGVs
            var detail = JsonSerializer.Deserialize<SimMapDetailDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (detail != null)
            {
                var graph = SimMapGraph.Build(detail);
                _sim.SetMapGraph(graph);
            }

            return Ok(new { message = $"Map {mapId} activated for all AGVs" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to activate map {MapId}", mapId);
            return StatusCode(503, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Load an additional map graph into all AGVs without replacing the primary map.
    /// Used for cross-map navigation simulation.
    /// </summary>
    [HttpPost("{mapId}/load")]
    public async Task<IActionResult> LoadAdditionalMap(string mapId)
    {
        try
        {
            var url = $"{_config.AcsApiBaseUrl}/api/vda5050/maps/{mapId}";
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var detail = JsonSerializer.Deserialize<SimMapDetailDto>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (detail != null)
            {
                var graph = SimMapGraph.Build(detail);
                _sim.AddMapGraph(graph);
            }

            return Ok(new { message = $"Map {mapId} loaded as additional map for all AGVs" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load additional map {MapId}", mapId);
            return StatusCode(503, new { message = ex.Message });
        }
    }

    /// <summary>
    /// Load all available maps into AGVs for cross-map navigation simulation.
    /// </summary>
    [HttpPost("load-all")]
    public async Task<IActionResult> LoadAllMaps()
    {
        try
        {
            var url = $"{_config.AcsApiBaseUrl}/api/vda5050/maps";
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var mapsJson = await response.Content.ReadAsStringAsync();

            var maps = JsonSerializer.Deserialize<List<SimMapSummaryDto>>(mapsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (maps == null || maps.Count == 0)
                return Ok(new { message = "No maps found", loaded = 0 });

            int loaded = 0;
            foreach (var map in maps)
            {
                try
                {
                    var detailUrl = $"{_config.AcsApiBaseUrl}/api/vda5050/maps/{map.MapId}";
                    var detailResponse = await _http.GetAsync(detailUrl);
                    detailResponse.EnsureSuccessStatusCode();
                    var detailJson = await detailResponse.Content.ReadAsStringAsync();

                    var detail = JsonSerializer.Deserialize<SimMapDetailDto>(detailJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (detail != null)
                    {
                        var graph = SimMapGraph.Build(detail);
                        if (loaded == 0)
                            _sim.SetMapGraph(graph); // First map becomes primary
                        else
                            _sim.AddMapGraph(graph);
                        loaded++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load map {MapId}, skipping", map.MapId);
                }
            }

            return Ok(new { message = $"Loaded {loaded}/{maps.Count} maps for all AGVs", loaded, total = maps.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all maps");
            return StatusCode(503, new { message = ex.Message });
        }
    }
}
