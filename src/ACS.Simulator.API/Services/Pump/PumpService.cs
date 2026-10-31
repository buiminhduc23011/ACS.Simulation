using System.Text.Json;

namespace ACS.Simulator.API.Services.Pump;

/// <summary>Giữ config + runtime của pump; persist vào data/pump-state.json (schema tương thích acs-pump cũ).</summary>
public sealed class PumpService
{
    public const string ApiKeyHeader = "X-ACS-API-Key";
    private const string IntegrationApiPath = "/api/integration/v1";
    private const int MaxEvents = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PumpService> _logger;
    private readonly SimulatorConfig _simulatorConfig;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private CancellationTokenSource _wakeup = new();

    private string? _baseUrlOverride;
    private string _apiKey = "";
    private int _intervalSeconds = 10;
    private int _targetRunning = 5;
    private int _lastPickedId;
    private bool _enabled;
    private List<int> _workflowIds = [];
    private readonly Dictionary<int, WorkflowRuntime> _runtimes = new();
    private readonly List<string> _events = [];
    private bool _acsReachable;
    private string? _acsVersion;
    private DateTime? _lastTickAtUtc;
    private DateTime? _nextTickAtUtc;

    public PumpService(IHttpClientFactory httpClientFactory, ILogger<PumpService> logger, SimulatorConfig simulatorConfig)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _simulatorConfig = simulatorConfig;
        _stateFile = Path.Combine(AppContext.BaseDirectory, "data", "pump-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        Load();
    }

    /// <summary>BaseUrl override nếu user set; mặc định theo ACS API của simulator.</summary>
    private string EffectiveBaseUrl =>
        !string.IsNullOrWhiteSpace(_baseUrlOverride) ? _baseUrlOverride : _simulatorConfig.AcsApiBaseUrl;

    public PumpSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new PumpSnapshot(
                _enabled,
                EffectiveBaseUrl,
                !string.IsNullOrWhiteSpace(_apiKey),
                _intervalSeconds,
                _targetRunning,
                _runtimes.Values.Count(IsBusy),
                [.. _workflowIds],
                _runtimes.Values.OrderBy(r => r.WorkflowId).ToList(),
                [.. _events],
                _acsReachable,
                _acsVersion,
                _lastTickAtUtc,
                _nextTickAtUtc,
                DateTime.UtcNow);
        }
    }

    /// <summary>Cập nhật config; field null giữ giá trị cũ (key rỗng = không đổi key).</summary>
    public void UpdateConfig(PumpConfigRequest request)
    {
        lock (_gate)
        {
            if (request.BaseUrl is not null)
            {
                var trimmed = request.BaseUrl.Trim().TrimEnd('/');
                _baseUrlOverride = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
            }
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
                _apiKey = request.ApiKey.Trim();
            if (request.IntervalSeconds is int seconds)
            {
                _intervalSeconds = Math.Max(seconds, 3);
                if (_enabled)
                    _nextTickAtUtc = DateTime.UtcNow + TimeSpan.FromSeconds(_intervalSeconds);
            }
            if (request.TargetRunning is int target)
                _targetRunning = Math.Clamp(target, 0, 1000);
            if (request.WorkflowIds is not null)
            {
                _workflowIds = request.WorkflowIds.Distinct().OrderBy(id => id).ToList();
                foreach (var staleId in _runtimes.Keys.Where(id => !_workflowIds.Contains(id)).ToList())
                    _runtimes.Remove(staleId);
            }
            KickLocked();
            Save();
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            AddEventLocked(enabled ? "pump ON" : "pump OFF");
            _nextTickAtUtc = enabled ? DateTime.UtcNow : null;
            KickLocked();
            Save();
        }
    }

    /// <summary>Worker gọi sau mỗi vòng để UI đếm ngược tới lần gọi tiếp theo.</summary>
    public void SetNextTick(DateTime nextTickAtUtc)
    {
        lock (_gate) _nextTickAtUtc = nextTickAtUtc;
    }

    /// <summary>Token để worker ngủ mà vẫn bị đánh thức ngay khi đổi config / bật-tắt pump.</summary>
    public CancellationToken GetWakeToken()
    {
        lock (_gate) return _wakeup.Token;
    }

    /// <summary>Chọn workflow rảnh tiếp theo theo vòng tròn (sau id đã start lần trước).</summary>
    private int? PickNextRoundRobin(List<int> orderedIds, List<int> free)
    {
        if (free.Count == 0 || orderedIds.Count == 0)
            return null;
        int last;
        lock (_gate) last = _lastPickedId;
        var freeSet = new HashSet<int>(free);
        var start = 0;
        var lastIdx = orderedIds.IndexOf(last);
        if (lastIdx >= 0)
            start = (lastIdx + 1) % orderedIds.Count;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[(start + i) % orderedIds.Count];
            if (freeSet.Contains(id))
            {
                lock (_gate) _lastPickedId = id;
                return id;
            }
        }
        return null;
    }

    private void KickLocked()
    {
        try { _wakeup.Cancel(); } catch { }
        try { _wakeup.Dispose(); } catch { }
        _wakeup = new CancellationTokenSource();
    }

    /// <summary>Lấy danh sách workflow active từ ACS bằng key đã lưu.</summary>
    public async Task<List<WorkflowSummary>> ListWorkflowsAsync(CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey) = GetConnection();
        var client = _httpClientFactory.CreateClient("PumpAcs");
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/workflows", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ACS returned {(int)response.StatusCode}: {Shorten(body)}");
        var workflows = new List<WorkflowSummary>();
        foreach (var item in JsonDocument.Parse(body).RootElement.EnumerateArray())
        {
            if (TryGetProperty(item, "id", out var id) && TryGetProperty(item, "name", out var name))
                workflows.Add(new WorkflowSummary(id.GetInt32(), name.GetString() ?? ""));
        }
        return workflows;
    }

    /// <summary>
    /// Mỗi tick: health-check ACS, check song song state tất cả workflow, đếm số đang bận.
    /// Chưa đủ TargetRunning thì start đúng 1 workflow rảnh tiếp theo theo vòng tròn.
    /// </summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _lastTickAtUtc = DateTime.UtcNow;
        var (baseUrl, apiKey) = GetConnection();
        var ids = GetWorkflowIds();
        int target;
        lock (_gate) target = _targetRunning;
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(apiKey))
            return;

        var client = _httpClientFactory.CreateClient("PumpAcs");
        if (!await CheckAcsHealthAsync(client, baseUrl, apiKey, cancellationToken))
        {
            AddEvent("tick: ACS không kết nối được, bỏ qua");
            return;
        }

        // Check song song — nhanh hơn nhiều so với tuần tự khi pump nhiều workflow.
        // busy: null = check lỗi → skip workflow đó tick này (không tính active, không start bù).
        var checks = ids.Select(async workflowId =>
        {
            try
            {
                var lastTxId = GetLastTransactionId(workflowId);
                if (lastTxId <= 0)
                    return (workflowId, busy: (bool?)false);
                var tx = await GetTransactionAsync(client, baseUrl, apiKey, lastTxId, cancellationToken);
                UpdateRuntime(workflowId, r =>
                {
                    r.LastState = tx.State;
                    r.LastStateCode = tx.StateCode;
                    r.PendingReason = tx.PendingReason;
                    r.AgvName = tx.AgvName;
                    r.CommandAccepted = tx.CommandAccepted;
                    r.LastCheckedAt = DateTime.UtcNow;
                    r.Note = tx.Busy ? "đang bận, bỏ qua" : null;
                });
                return (workflowId, busy: (bool?)tx.Busy);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                UpdateRuntime(workflowId, r => r.Note = Shorten(ex.Message));
                AddEvent($"wf {workflowId} check: {Shorten(ex.Message)}");
                return (workflowId, busy: (bool?)null);
            }
        });
        var results = await Task.WhenAll(checks);
        var free = results.Where(r => r.busy == false).Select(r => r.workflowId).ToList();
        var active = results.Count(r => r.busy == true);

        if (target <= 0 || active >= target)
        {
            if (target > 0)
                AddEvent($"tick: đang {active}/{target} running, không start thêm");
            Save();
            return;
        }
        if (free.Count == 0)
        {
            AddEvent("tick: không còn workflow rảnh");
            Save();
            return;
        }

        var nextId = PickNextRoundRobin(ids, free);
        if (!nextId.HasValue)
        {
            Save();
            return;
        }
        await StartPickedAsync(client, baseUrl, apiKey, nextId.Value, cancellationToken);
        Save();
    }

    /// <summary>Hủy 1 transaction đang chạy trên ACS (dùng từ UI).</summary>
    public async Task<string> CancelTransactionAsync(long transactionId, CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey) = GetConnection();
        var client = _httpClientFactory.CreateClient("PumpAcs");
        using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{IntegrationApiPath}/workflows/transactions/{transactionId}/cancel", apiKey);
        request.Content = new StringContent("{\"reason\":\"Cancelled from simulator pump\"}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
            throw new InvalidOperationException($"cancel failed {(int)response.StatusCode}: {Shorten(body)}");
        AddEvent($"tx {transactionId} cancel -> {(int)response.StatusCode}");
        _logger.LogInformation("Pump cancelled transaction {TransactionId}", transactionId);
        return body;
    }

    private async Task<bool> CheckAcsHealthAsync(HttpClient client, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/health", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            var reachable = response.IsSuccessStatusCode;
            string? version = null;
            if (reachable)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (TryGetProperty(JsonDocument.Parse(body).RootElement, "version", out var v))
                    version = v.GetString();
            }
            lock (_gate) { _acsReachable = reachable; _acsVersion = version; }
            return reachable;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lock (_gate) { _acsReachable = false; _acsVersion = null; }
            UpdateAllRuntimeNotes(Shorten(ex.Message));
            return false;
        }
    }

    private void UpdateAllRuntimeNotes(string note)
    {
        lock (_gate)
            foreach (var r in _runtimes.Values)
                r.Note = note;
    }

    private async Task StartPickedAsync(HttpClient client, string baseUrl, string apiKey, int workflowId, CancellationToken cancellationToken)
    {
        try
        {
            var seq = GetStartedCount(workflowId) + 1;
            var started = await StartWorkflowAsync(client, baseUrl, apiKey, workflowId, $"pump-{workflowId}-{seq}", cancellationToken);
            UpdateRuntime(workflowId, r =>
            {
                r.LastTransactionId = started.TransactionId;
                r.LastState = started.Status;
                r.LastStateCode = null;
                r.PendingReason = null;
                r.AgvName = null;
                r.CommandAccepted = null;
                r.LastCheckedAt = DateTime.UtcNow;
                r.StartedCount++;
                r.Note = null;
            });
            AddEvent($"wf {workflowId} start -> tx {started.TransactionId} ({started.Status})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            UpdateRuntime(workflowId, r => r.Note = Shorten(ex.Message));
            AddEvent($"wf {workflowId} error: {Shorten(ex.Message)}");
        }
    }

    private int GetStartedCount(int workflowId)
    {
        lock (_gate)
            return _runtimes.TryGetValue(workflowId, out var r) ? r.StartedCount : 0;
    }

    private sealed record TxInfo(string? State, int? StateCode, string? PendingReason, string? AgvName, bool? CommandAccepted, bool Busy);

    // GET trạng thái transaction; 404 coi như đã xong → tick sẽ start lại.
    private static async Task<TxInfo> GetTransactionAsync(
        HttpClient client, string baseUrl, string apiKey, long transactionId, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/workflows/transactions/{transactionId}", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new TxInfo("NotFound", null, null, null, null, Busy: false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"check tx {transactionId} failed {(int)response.StatusCode}: {Shorten(body)}");
        var json = JsonDocument.Parse(body).RootElement;
        var state = TryGetProperty(json, "state", out var s) ? s.GetString() : null;
        int? stateCode = TryGetProperty(json, "stateCode", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : null;
        var pendingReason = TryGetProperty(json, "pendingReason", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;
        var agvName = TryGetProperty(json, "agvName", out var an) && an.ValueKind == JsonValueKind.String ? an.GetString() : null;
        bool? commandAccepted = TryGetProperty(json, "commandAccepted", out var ca) && ca.ValueKind is JsonValueKind.True or JsonValueKind.False ? ca.GetBoolean() : null;
        // Busy theo stateCode mới (1 Pending, 2 Running); fallback chuỗi cho ACS cũ.
        var busy = stateCode is 1 or 2 || state is "Pending" or "Created" or "Running";
        return new TxInfo(state, stateCode, pendingReason, agvName, commandAccepted, busy);
    }

    // POST start 1 instance mới, không gán AGV; correlationId giúp retry idempotent.
    private static async Task<StartedWorkflow> StartWorkflowAsync(
        HttpClient client, string baseUrl, string apiKey, int workflowId, string correlationId, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{IntegrationApiPath}/workflows/{workflowId}/start", apiKey);
        request.Content = new StringContent($"{{\"correlationId\":\"{correlationId}\"}}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"start failed {(int)response.StatusCode}: {Shorten(body)}");
        var json = JsonDocument.Parse(body).RootElement;
        var txId = TryGetProperty(json, "transactionId", out var tx) ? tx.GetInt64() : 0L;
        var status = TryGetProperty(json, "status", out var st) ? st.GetString() ?? "?" : "?";
        return new StartedWorkflow(txId, status);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        return request;
    }

    private static bool IsBusy(WorkflowRuntime r)
        => r.LastStateCode is 1 or 2 || r.LastState is "Pending" or "Created" or "Running";

    private long GetLastTransactionId(int workflowId)
    {
        lock (_gate)
            return _runtimes.TryGetValue(workflowId, out var r) ? r.LastTransactionId : 0;
    }

    private void UpdateRuntime(int workflowId, Action<WorkflowRuntime> update)
    {
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(workflowId, out var runtime))
            {
                runtime = new WorkflowRuntime { WorkflowId = workflowId };
                _runtimes[workflowId] = runtime;
            }
            update(runtime);
        }
    }

    private List<int> GetWorkflowIds()
    {
        lock (_gate) return [.. _workflowIds];
    }

    private (string BaseUrl, string ApiKey) GetConnection()
    {
        lock (_gate) return (EffectiveBaseUrl, _apiKey);
    }

    private void AddEvent(string message)
    {
        lock (_gate) AddEventLocked(message);
    }

    private void AddEventLocked(string message)
    {
        _events.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        if (_events.Count > MaxEvents)
            _events.RemoveAt(_events.Count - 1);
    }

    private void Save()
    {
        int lastPicked;
        lock (_gate) lastPicked = _lastPickedId;
        var state = new PersistedState
        {
            BaseUrl = _baseUrlOverride,
            ApiKey = _apiKey,
            IntervalSeconds = _intervalSeconds,
            TargetRunning = _targetRunning,
            Enabled = _enabled,
            WorkflowIds = _workflowIds,
            LastPickedId = lastPicked,
            Runtimes = new Dictionary<int, WorkflowRuntime>(_runtimes)
        };
        try
        {
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // Lỗi ghi file không được chặn pump.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_stateFile))
                return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_stateFile));
            if (state is null)
                return;
            _baseUrlOverride = state.BaseUrl;
            _apiKey = state.ApiKey ?? "";
            _intervalSeconds = Math.Max(state.IntervalSeconds, 3);
            _targetRunning = Math.Clamp(state.TargetRunning, 0, 1000);
            _enabled = state.Enabled;
            _workflowIds = state.WorkflowIds ?? [];
            _lastPickedId = state.LastPickedId;
            foreach (var (id, runtime) in state.Runtimes ?? [])
                _runtimes[id] = runtime;
        }
        catch
        {
            // File hỏng thì chạy với default.
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string Shorten(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : text.Length <= 160 ? text : text[..160];

    // Schema khớp acs-pump.json cũ để file state migrate drop-in.
    private sealed class PersistedState
    {
        public string? BaseUrl { get; set; }
        public string ApiKey { get; set; } = "";
        public int IntervalSeconds { get; set; } = 10;
        public int TargetRunning { get; set; } = 5;
        public bool Enabled { get; set; }
        public List<int> WorkflowIds { get; set; } = [];
        public int LastPickedId { get; set; }
        public Dictionary<int, WorkflowRuntime> Runtimes { get; set; } = new();
    }
}

public sealed record StartedWorkflow(long TransactionId, string Status);
