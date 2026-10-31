namespace ACS.Simulator.API.Services.Pump;

// Request DTOs

/// <summary>Field null giữ giá trị cũ; apiKey rỗng = không đổi key, baseUrl rỗng = về mặc định ACS API.</summary>
public sealed record PumpConfigRequest(
    string? BaseUrl,
    string? ApiKey,
    int? IntervalSeconds,
    int? TargetRunning,
    List<int>? WorkflowIds);

public sealed record PumpEnabledRequest(bool Enabled);

// ACS integration DTOs

public sealed record WorkflowSummary(int Id, string Name);

// Runtime + snapshot

/// <summary>Trạng thái 1 workflow đang pump; giữ tên field schema acs-pump.json cũ để migrate drop-in.</summary>
public sealed class WorkflowRuntime
{
    public int WorkflowId { get; set; }
    public long LastTransactionId { get; set; }
    public string? LastState { get; set; }
    public int? LastStateCode { get; set; }
    public string? PendingReason { get; set; }
    public string? AgvName { get; set; }
    public bool? CommandAccepted { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public string? Note { get; set; }
    public int StartedCount { get; set; }
}

/// <summary>Snapshot bất biến cho API đọc.</summary>
public sealed record PumpSnapshot(
    bool Enabled,
    string BaseUrl,
    bool HasApiKey,
    int IntervalSeconds,
    int TargetRunning,
    int RunningCount,
    List<int> WorkflowIds,
    List<WorkflowRuntime> Runtimes,
    List<string> Events,
    bool AcsReachable,
    string? AcsVersion,
    DateTime? LastTickAtUtc,
    DateTime? NextTickAtUtc,
    DateTime ServerTimeUtc);
