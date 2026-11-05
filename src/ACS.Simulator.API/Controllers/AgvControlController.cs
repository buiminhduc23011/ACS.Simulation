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
        if (req.Speed <= 0 || req.Speed > 10)
            return BadRequest(new { message = "Speed must be within (0, 10] m/s." });
        if (_sim.SetSpeed(id, req.Speed)) return Ok();
        return NotFound();
    }

    /// <summary>
    /// Cài trần tốc độ + gia tốc tăng/giảm tốc. Edge cho cao hơn vẫn bị kẹp bởi MaxSpeed.
    /// </summary>
    [HttpPost("motion-config")]
    public IActionResult SetMotionConfig(string id, [FromBody] SetMotionConfigRequest req)
    {
        if (req.MaxSpeed <= 0 || req.MaxSpeed > 10)
            return BadRequest(new { message = "MaxSpeed must be within (0, 10] m/s." });
        if (req.Acceleration <= 0 || req.Acceleration > 10)
            return BadRequest(new { message = "Acceleration must be within (0, 10] m/s²." });
        if (req.Deceleration <= 0 || req.Deceleration > 10)
            return BadRequest(new { message = "Deceleration must be within (0, 10] m/s²." });
        if (_sim.SetMotionConfig(id, req.MaxSpeed, req.Acceleration, req.Deceleration))
            return Ok(new { req.MaxSpeed, req.Acceleration, req.Deceleration });
        return NotFound();
    }

    /// <summary>
    /// Cài thời gian thực hiện action PICK/LIFT, DROP/LOWER, generic và delay cập nhật load.
    /// </summary>
    [HttpPost("actions-config")]
    public IActionResult SetActionsConfig(string id, [FromBody] SetActionsConfigRequest req)
    {
        if (req.LiftDurationMs < 0 || req.LiftDurationMs > 600000)
            return BadRequest(new { message = "LiftDurationMs must be within [0, 600000] ms." });
        if (req.LowerDurationMs < 0 || req.LowerDurationMs > 600000)
            return BadRequest(new { message = "LowerDurationMs must be within [0, 600000] ms." });
        if (req.GenericActionDurationMs < 0 || req.GenericActionDurationMs > 600000)
            return BadRequest(new { message = "GenericActionDurationMs must be within [0, 600000] ms." });
        if (req.LoadStateDelayMs < 0 || req.LoadStateDelayMs > 60000)
            return BadRequest(new { message = "LoadStateDelayMs must be within [0, 60000] ms." });
        if (_sim.SetActionsConfig(id, req.LiftDurationMs, req.LowerDurationMs, req.GenericActionDurationMs, req.LoadStateDelayMs))
            return Ok(new { req.LiftDurationMs, req.LowerDurationMs, req.GenericActionDurationMs, req.LoadStateDelayMs });
        return NotFound();
    }

    /// <summary>
    /// Switch VDA5050 operatingMode (AUTOMATIC / MANUAL).
    /// Chặn khi xe còn order/action chưa xong; MANUAL lúc rảnh sẽ xoá mission để chờ ACS gửi lại.
    /// </summary>
    [HttpPost("operating-mode")]
    public async Task<IActionResult> SetOperatingMode(string id, [FromBody] SetOperatingModeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Mode))
            return BadRequest(new { message = "Mode is required (AUTOMATIC or MANUAL)" });
        var applied = await _sim.SetOperatingModeAsync(id, req.Mode);
        if (applied is null) return NotFound();
        if (applied == false)
            return Conflict(new { message = "AGV still has an active order/action — finish or cancel it before switching operating mode" });
        return Ok(new { mode = req.Mode });
    }

    /// <summary>
    /// Manually clear the current orderId and orderUpdateId from the AGV state.
    /// </summary>
    [HttpPost("clear-order")]
    public IActionResult ClearOrderState(string id)
    {
        if (_sim.ClearOrderState(id)) return Ok();
        return NotFound();
    }

    /// <summary>
    /// Test hook: keep PICK/DROP/WAIT actions at WAITING instead of FINISHED.
    /// </summary>
    [HttpPost("action-hold")]
    public IActionResult SetActionHold(string id, [FromBody] SetActionHoldRequest req)
    {
        if (_sim.SetActionHold(id, req.Enabled)) return Ok(new { enabled = req.Enabled });
        return NotFound();
    }

    /// <summary>
    /// Giả lập nghẽn mạng: delay giây trước khi order MQTT được echo trong state.
    /// 0 = tắt. Tối đa 300s.
    /// </summary>
    [HttpPost("order-echo-delay")]
    public IActionResult SetOrderEchoDelay(string id, [FromBody] SetOrderEchoDelayRequest req)
    {
        if (_sim.SetOrderEchoDelay(id, req.DelaySeconds)) return Ok(new { delaySeconds = req.DelaySeconds });
        return NotFound();
    }

    /// <summary>
    /// Recent ACS MQTT payloads (order + instantActions) for debug on the control page.
    /// </summary>
    [HttpGet("inbound-messages")]
    public IActionResult GetInboundMessages(string id)
    {
        var messages = _sim.GetInboundMessages(id);
        if (messages == null) return NotFound();
        return Ok(messages);
    }

    /// <summary>
    /// Outbound state payload (VDA5050 wire format) the AGV is currently publishing.
    /// </summary>
    [HttpGet("state-payload")]
    public IActionResult GetStatePayload(string id)
    {
        var state = _sim.GetPublishedState(id);
        if (state == null) return NotFound();
        return Ok(state);
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

    [HttpDelete("loads")]
    public async Task<IActionResult> ClearLoads(string id)
    {
        var cleared = await _sim.ClearLoadsAsync(id);
        if (cleared < 0) return NotFound();
        return Ok(new { cleared });
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
