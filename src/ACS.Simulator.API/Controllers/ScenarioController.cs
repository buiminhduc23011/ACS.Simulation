using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/scenarios")]
public class ScenarioController : ControllerBase
{
    private readonly ScenarioRunnerService _runner;
    public ScenarioController(ScenarioRunnerService runner) => _runner = runner;

    [HttpGet]
    public IActionResult GetAll() => Ok(_runner.GetAll());

    [HttpPost("import")]
    public async Task<IActionResult> Import(IFormFile file)
    {
        if (file == null || !file.FileName.EndsWith(".json"))
            return BadRequest("JSON file required");
        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync();
        var id = await _runner.ImportAsync(file.FileName, json);
        return Ok(new { id });
    }

    [HttpGet("{id}/download")]
    public IActionResult Download(string id)
    {
        try
        {
            var json = _runner.GetJson(id);
            return Content(json, "application/json");
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("{id}/run")]
    public async Task<IActionResult> Run(string id, [FromBody] RunScenarioRequest? req)
    {
        try
        {
            var run = await _runner.RunAsync(id, req?.AgvId);
            return Ok(run);
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _runner.Stop();
        return Ok(new { message = "Stopped" });
    }

    [HttpGet("current-run")]
    public IActionResult CurrentRun()
    {
        var run = _runner.GetCurrentRun();
        if (run == null) return NotFound();
        return Ok(run);
    }
}
