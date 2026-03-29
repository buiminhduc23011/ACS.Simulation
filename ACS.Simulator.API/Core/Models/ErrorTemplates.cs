namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// Predefined error templates với side-effects tương ứng.
/// Dùng trong Error Injection UI và Scenario Scripting.
/// </summary>
public class ErrorTemplate
{
    public string Name { get; set; } = string.Empty;
    public string ErrorType { get; set; } = string.Empty;
    public string ErrorLevel { get; set; } = "WARNING";
    public string Description { get; set; } = string.Empty;
    public ErrorSideEffect SideEffect { get; set; } = ErrorSideEffect.None;
}

public enum ErrorSideEffect
{
    None,
    ReduceSpeed50Percent,
    StopMovement,
    EmergencyStop,
    LocalizationLost,
    ClearLoads
}

public static class ErrorTemplates
{
    public static readonly IReadOnlyList<ErrorTemplate> All = new List<ErrorTemplate>
    {
        new ErrorTemplate
        {
            Name = "SENSOR_FAILURE",
            ErrorType = "sensor",
            ErrorLevel = "WARNING",
            Description = "Sensor malfunction detected - reporting only",
            SideEffect = ErrorSideEffect.None
        },
        new ErrorTemplate
        {
            Name = "MOTOR_OVERHEAT",
            ErrorType = "motor",
            ErrorLevel = "WARNING",
            Description = "Motor temperature exceeds safe limit - reducing speed",
            SideEffect = ErrorSideEffect.ReduceSpeed50Percent
        },
        new ErrorTemplate
        {
            Name = "OBSTACLE_DETECTED",
            ErrorType = "navigation",
            ErrorLevel = "WARNING",
            Description = "Obstacle detected in path - stopping movement temporarily",
            SideEffect = ErrorSideEffect.StopMovement
        },
        new ErrorTemplate
        {
            Name = "EMERGENCY_STOP",
            ErrorType = "safety",
            ErrorLevel = "FATAL",
            Description = "Emergency stop triggered - eStop = MANUAL",
            SideEffect = ErrorSideEffect.EmergencyStop
        },
        new ErrorTemplate
        {
            Name = "LOCALIZATION_LOST",
            ErrorType = "localization",
            ErrorLevel = "FATAL",
            Description = "AGV position lost - positionInitialized = false",
            SideEffect = ErrorSideEffect.LocalizationLost
        },
        new ErrorTemplate
        {
            Name = "BATTERY_LOW",
            ErrorType = "battery",
            ErrorLevel = "WARNING",
            Description = "Battery below minimum level",
            SideEffect = ErrorSideEffect.None
        },
        new ErrorTemplate
        {
            Name = "LOAD_DROPPED",
            ErrorType = "load",
            ErrorLevel = "FATAL",
            Description = "Load dropped unexpectedly - clearing loads and stopping",
            SideEffect = ErrorSideEffect.ClearLoads
        },
        new ErrorTemplate
        {
            Name = "COMMUNICATION_ERROR",
            ErrorType = "communication",
            ErrorLevel = "WARNING",
            Description = "Communication failure with peripheral device",
            SideEffect = ErrorSideEffect.None
        }
    };

    public static ErrorTemplate? GetByName(string name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
