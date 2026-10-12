using ACS.Simulator.API.Core.Models;
using ACS.Simulator.API.Models;
using ACS.Simulator.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

[ApiController]
[Route("api/simulator/maps")]
public class MapsController : ControllerBase
{
    private readonly SimulatorService _sim;
    public MapsController(SimulatorService sim) => _sim = sim;

    /// <summary>Danh sách map graph sim đã load từ ACS.</summary>
    [HttpGet]
    public ActionResult<List<SimMapInfoDto>> GetMaps() => Ok(_sim.GetKnownMaps());

    /// <summary>Node list của map (kèm rfidTag) để UI teleport AGV lên node.</summary>
    [HttpGet("{mapId}/nodes")]
    public ActionResult<List<SimNodeDto>> GetNodes(string mapId)
    {
        var nodes = _sim.GetMapNodes(mapId);
        if (nodes == null) return NotFound();
        return Ok(nodes);
    }
}
