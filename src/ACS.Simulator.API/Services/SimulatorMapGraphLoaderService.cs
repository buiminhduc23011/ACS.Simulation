using ACS.Simulator.API.Core.Models;
using System.Net.Http.Json;
using System.Text.Json;

namespace ACS.Simulator.API.Services;

/// <summary>
/// Loads ACS map graphs into the simulator on startup so route validation does not
/// depend on an operator opening the map page or calling the map activation API.
/// </summary>
public sealed class SimulatorMapGraphLoaderService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SimulatorService _simulator;
    private readonly SimulatorConfig _config;
    private readonly ILogger<SimulatorMapGraphLoaderService> _logger;

    public SimulatorMapGraphLoaderService(
        IHttpClientFactory httpClientFactory,
        SimulatorService simulator,
        SimulatorConfig config,
        ILogger<SimulatorMapGraphLoaderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _simulator = simulator;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_config.AcsApiBaseUrl))
        {
            _logger.LogWarning("Simulator map graph auto-load skipped: ACS API base URL is empty.");
            return;
        }

        for (var attempt = 1; attempt <= 6 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                if (await TryLoadAllMapsAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Simulator map graph auto-load attempt {Attempt} failed.",
                    attempt);
            }

            var delaySeconds = Math.Min(30, attempt * 5);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }

        _logger.LogWarning(
            "Simulator map graph auto-load gave up after retries. Movement topology validation will activate when maps are loaded manually.");
    }

    private async Task<bool> TryLoadAllMapsAsync(CancellationToken stoppingToken)
    {
        var http = _httpClientFactory.CreateClient("AcsApi");
        var baseUrl = _config.AcsApiBaseUrl.TrimEnd('/');
        var mapsUrl = $"{baseUrl}/api/vda5050/maps";
        var maps = await http.GetFromJsonAsync<List<SimMapSummaryDto>>(
            mapsUrl,
            JsonOptions,
            stoppingToken);

        if (maps == null || maps.Count == 0)
        {
            _logger.LogInformation("Simulator map graph auto-load found no maps at {Url}.", mapsUrl);
            return false;
        }

        var loaded = 0;
        foreach (var map in maps)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return false;
            }

            var mapId = !string.IsNullOrWhiteSpace(map.MapId)
                ? map.MapId
                : map.Id.ToString();
            try
            {
                var detailUrl = $"{baseUrl}/api/vda5050/maps/{Uri.EscapeDataString(mapId)}";
                var detail = await http.GetFromJsonAsync<SimMapDetailDto>(
                    detailUrl,
                    JsonOptions,
                    stoppingToken);
                if (detail == null)
                {
                    _logger.LogWarning("Simulator map graph auto-load skipped MapId={MapId}: empty detail response.", mapId);
                    continue;
                }

                var graph = SimMapGraph.Build(detail);
                if (loaded == 0)
                {
                    _simulator.SetMapGraph(graph);
                }
                else
                {
                    _simulator.AddMapGraph(graph);
                }

                loaded++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Simulator map graph auto-load skipped MapId={MapId}.",
                    mapId);
            }
        }

        _logger.LogInformation(
            "Simulator map graph auto-load completed: loaded {Loaded}/{Total} map(s).",
            loaded,
            maps.Count);
        return loaded > 0;
    }
}
