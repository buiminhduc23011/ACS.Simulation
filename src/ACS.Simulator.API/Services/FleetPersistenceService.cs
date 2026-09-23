using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACS.Simulator.API.Services;

/// <summary>
/// Persists AGV fleet configuration to a JSON file so AGVs survive restarts.
/// </summary>
public class FleetPersistenceService
{
    private readonly ILogger<FleetPersistenceService> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FleetPersistenceService(ILogger<FleetPersistenceService> logger)
    {
        _logger = logger;
        // Store fleet config next to the executable
        var baseDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(baseDir, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "fleet.json");
    }

    /// <summary>
    /// Save the current fleet configuration to disk.
    /// </summary>
    public async Task SaveAsync(IEnumerable<SavedAgvConfig> agvConfigs)
    {
        await _fileLock.WaitAsync();
        try
        {
            var data = new FleetData
            {
                SavedAt = DateTime.UtcNow,
                Agvs = agvConfigs.ToList()
            };

            var json = JsonSerializer.Serialize(data, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);

            _logger.LogInformation("[FleetPersistence] Saved {Count} AGV(s) to {Path}", data.Agvs.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FleetPersistence] Failed to save fleet config");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Load fleet configuration from disk. Returns empty list if no file exists.
    /// </summary>
    public async Task<List<SavedAgvConfig>> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger.LogInformation("[FleetPersistence] No saved fleet found at {Path}", _filePath);
                return new List<SavedAgvConfig>();
            }

            var json = await File.ReadAllTextAsync(_filePath);
            var data = JsonSerializer.Deserialize<FleetData>(json, JsonOptions);

            if (data?.Agvs == null || data.Agvs.Count == 0)
            {
                _logger.LogInformation("[FleetPersistence] Fleet file exists but contains no AGVs");
                return new List<SavedAgvConfig>();
            }

            _logger.LogInformation("[FleetPersistence] Loaded {Count} AGV(s) from {Path} (saved at {SavedAt})",
                data.Agvs.Count, _filePath, data.SavedAt);

            return data.Agvs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FleetPersistence] Failed to load fleet config from {Path}", _filePath);
            return new List<SavedAgvConfig>();
        }
    }
}

/// <summary>
/// Root object for fleet persistence file.
/// </summary>
public class FleetData
{
    public DateTime SavedAt { get; set; }
    public List<SavedAgvConfig> Agvs { get; set; } = new();
}

/// <summary>
/// Persisted AGV configuration — everything needed to recreate an AGV on startup.
/// </summary>
public class SavedAgvConfig
{
    public string SerialNumber { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string IpAddress { get; set; } = "192.168.1.100";
    public string MacAddress { get; set; } = "00:00:00:00:00:00";
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double PosTheta { get; set; }
    public string MapId { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    public bool AutoStart { get; set; } = true;
    public List<SavedAgvMapMapping> MapMappings { get; set; } = new();
}

public class SavedAgvMapMapping
{
    public string SourceMapId { get; set; } = string.Empty;
    public string TargetMapId { get; set; } = string.Empty;
}
