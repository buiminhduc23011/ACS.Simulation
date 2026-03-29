using System.Text.Json;

namespace ACS.Simulator.API.Services;

public class SimulatorRuntimeConfigStore
{
    private readonly ILogger<SimulatorRuntimeConfigStore> _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SimulatorRuntimeConfigStore(ILogger<SimulatorRuntimeConfigStore> logger)
    {
        _logger = logger;
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, "runtime-config.json");
    }

    public async Task SaveAsync(SimulatorConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var payload = new RuntimeConfigFile
        {
            Simulator = new SimulatorConfig
            {
                MqttHost = config.MqttHost,
                MqttPort = config.MqttPort,
                AcsApiBaseUrl = config.AcsApiBaseUrl
            }
        };

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
            _logger.LogInformation("Saved simulator runtime config to {Path}", _filePath);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private sealed class RuntimeConfigFile
    {
        public SimulatorConfig Simulator { get; set; } = new();
    }
}
