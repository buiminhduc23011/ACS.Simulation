using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/config")]
public class ConfigController : ControllerBase
{
    private readonly SimulatorConfig _config;
    private readonly SimulatorRuntimeConfigStore _runtimeConfigStore;

    public ConfigController(SimulatorConfig config, SimulatorRuntimeConfigStore runtimeConfigStore)
    {
        _config = config;
        _runtimeConfigStore = runtimeConfigStore;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new SimulatorConfigDto(
            new MqttConfigDto(_config.MqttHost, _config.MqttPort),
            new AcsApiConfigDto(_config.AcsApiBaseUrl)
        ));
    }

    [HttpPut("mqtt")]
    public async Task<IActionResult> SetMqtt([FromBody] MqttConfigRequest req, CancellationToken cancellationToken)
    {
        var host = req.Host?.Trim();
        if (string.IsNullOrWhiteSpace(host))
            return BadRequest(new { message = "MQTT host is required." });
        if (req.Port is < 1 or > 65535)
            return BadRequest(new { message = "MQTT port must be between 1 and 65535." });

        _config.MqttHost = host;
        _config.MqttPort = req.Port;
        await _runtimeConfigStore.SaveAsync(_config, cancellationToken);
        return Ok(new { message = "MQTT config updated (effective on next AGV create)" });
    }

    [HttpPut("acs-api")]
    public async Task<IActionResult> SetAcsApi([FromBody] AcsApiConfigRequest req, CancellationToken cancellationToken)
    {
        var baseUrl = req.BaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return BadRequest(new { message = "ACS API base URL is required." });

        _config.AcsApiBaseUrl = baseUrl;
        await _runtimeConfigStore.SaveAsync(_config, cancellationToken);
        return Ok(new { message = "ACS API URL updated" });
    }
}
