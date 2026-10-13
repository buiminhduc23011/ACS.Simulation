namespace ACS.Simulator.API.Core.Models;

/// <summary>
/// Notification currently shown on the AGV screen (ACS → notifications topic).
/// Holds until a CLEAR message or duration expiry.
/// </summary>
public sealed record ScreenNotification(
    string Type,
    string Message,
    DateTime? ExpiresAtUtc);
