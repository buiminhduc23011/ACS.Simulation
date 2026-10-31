using ACS.Simulator.API.Services.Pump;
using Microsoft.AspNetCore.Mvc;

namespace ACS.Simulator.API.Controllers;

/// <summary>Workflow pump — giữ đúng số transaction running mục tiêu trên ACS qua Integration API.</summary>
[ApiController]
[Route("api/pump")]
public class PumpController : ControllerBase
{
    private readonly PumpService _pump;

    public PumpController(PumpService pump) => _pump = pump;

    [HttpGet("state")]
    public IActionResult GetState() => Ok(_pump.GetSnapshot());

    [HttpPut("config")]
    public IActionResult UpdateConfig([FromBody] PumpConfigRequest req)
    {
        _pump.UpdateConfig(req);
        return Ok(_pump.GetSnapshot());
    }

    [HttpPost("enabled")]
    public IActionResult SetEnabled([FromBody] PumpEnabledRequest req)
    {
        _pump.SetEnabled(req.Enabled);
        return Ok(_pump.GetSnapshot());
    }

    /// <summary>Proxy lấy danh sách workflow active từ ACS (để user chọn workflow pump).</summary>
    [HttpGet("workflows")]
    public async Task<IActionResult> ListWorkflows(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _pump.ListWorkflowsAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Hủy 1 transaction đang chạy trên ACS.</summary>
    [HttpPost("transactions/{transactionId:long}/cancel")]
    public async Task<IActionResult> CancelTransaction(long transactionId, CancellationToken cancellationToken)
    {
        try
        {
            var body = await _pump.CancelTransactionAsync(transactionId, cancellationToken);
            return Content(body, "application/json");
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
