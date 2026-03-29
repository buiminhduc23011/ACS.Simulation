using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/agvs")]
public class AgvController : ControllerBase
{
    private readonly SimulatorService _sim;
    public AgvController(SimulatorService sim) => _sim = sim;

    [HttpGet]
    public IActionResult GetAll() => Ok(_sim.GetFleetSummary());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgvRequest req)
    {
        var result = await _sim.CreateAgvAsync(req);
        if (result == null) return Conflict(new { message = $"AGV '{req.SerialNumber}' already exists" });
        return CreatedAtAction(nameof(GetState), new { id = result.Id }, result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateAgvRequest req)
    {
        var result = await _sim.UpdateAgvAsync(id, req);
        if (result == null) return NotFound(new { message = $"AGV '{id}' not found" });
        return Ok(result);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (await _sim.DeleteAgvAsync(id)) return NoContent();
        return NotFound();
    }

    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        if (await _sim.StartAgvAsync(id)) return Ok(new { message = "Started" });
        return NotFound();
    }

    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        if (await _sim.StopAgvAsync(id)) return Ok(new { message = "Stopped" });
        return NotFound();
    }

    [HttpGet("{id}/state")]
    public IActionResult GetState(string id)
    {
        var agv = _sim.Get(id);
        if (agv == null) return NotFound();
        var state = agv.CurrentState;
        var pos = state.AgvPosition;
        var dto = new AgvStateDto(
            agv.SerialNumber, state.Driving, state.Paused,
            pos.X, pos.Y, pos.Theta, pos.MapId ?? "",
            state.BatteryState.BatteryCharge, state.BatteryState.Charging,
            state.OperatingMode,
            agv.GetErrors().Select(e => new ErrorDto(e.ErrorType, e.ErrorLevel, e.ErrorDescription)).ToList(),
            state.SafetyState.EStop != "NONE"
        );
        return Ok(dto);
    }
}
