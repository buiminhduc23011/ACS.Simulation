using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/agvs/{id}/control")]
public class AgvControlController : ControllerBase
{
    private readonly SimulatorService _sim;
    public AgvControlController(SimulatorService sim) => _sim = sim;

    [HttpPost("position")]
    public IActionResult SetPosition(string id, [FromBody] SetPositionRequest req)
    {
        if (_sim.SetPosition(id, req)) return Ok();
        return NotFound();
    }

    [HttpPost("battery")]
    public IActionResult SetBattery(string id, [FromBody] SetBatteryRequest req)
    {
        if (_sim.SetBattery(id, req.Level)) return Ok();
        return NotFound();
    }

    [HttpPost("speed")]
    public IActionResult SetSpeed(string id, [FromBody] SetSpeedRequest req)
    {
        if (_sim.SetSpeed(id, req.Speed)) return Ok();
        return NotFound();
    }

    [HttpPost("lift")]
    public async Task<IActionResult> Lift(string id)
    {
        if (await _sim.LiftAsync(id)) return Ok(new { message = "Lifted" });
        return NotFound();
    }

    [HttpPost("lower")]
    public async Task<IActionResult> Lower(string id)
    {
        if (await _sim.LowerAsync(id)) return Ok(new { message = "Lowered" });
        return NotFound();
    }

    [HttpPost("errors")]
    public async Task<IActionResult> AddError(string id, [FromBody] AddErrorRequest req)
    {
        if (await _sim.AddErrorAsync(id, req)) return Ok();
        return NotFound();
    }

    [HttpDelete("errors")]
    public async Task<IActionResult> ClearErrors(string id)
    {
        var cleared = await _sim.ClearAllErrorsAsync(id);
        if (cleared < 0) return NotFound();
        return Ok(new { cleared });
    }

    [HttpPost("errors/inject")]
    public async Task<IActionResult> InjectTemplate(string id, [FromBody] InjectTemplateRequest req)
    {
        if (await _sim.InjectTemplateAsync(id, req.TemplateName)) return Ok();
        return BadRequest(new { message = "AGV not found or template unknown" });
    }

    [HttpPost("chaos")]
    public IActionResult SetChaos(string id, [FromBody] ChaosSettingsRequest req)
    {
        if (_sim.SetChaos(id, req)) return Ok();
        return NotFound();
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> TriggerDisconnect(string id, [FromBody] DisconnectRequest req)
    {
        if (await _sim.TriggerDisconnectAsync(id, req.DurationMs)) return Ok();
        return NotFound();
    }
}

[ApiController]
[Route("api/simulator/error-templates")]
public class ErrorTemplatesController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll()
    {
        var templates = ErrorTemplates.All.Select(t => new
        {
            t.Name,
            t.ErrorType,
            t.ErrorLevel,
            t.Description,
            SideEffect = t.SideEffect.ToString()
        });
        return Ok(templates);
    }
}
