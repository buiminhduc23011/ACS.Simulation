using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ACS.Simulator.API.Services.Pump;

/// <summary>
/// Giữ config + runtime của pump; persist vào data/pump-state.json (schema tương thích acs-pump cũ).
///
/// Chính sách chống spam ACS (ACS cache trạng thái transaction 15s, gộp RequestCount mỗi 60s):
/// - Mỗi transaction active chỉ poll tối đa 1 lần/15s + jitter ±20%; poll nhanh hơn không có dữ liệu mới.
/// - Một vòng poll duy nhất (PumpWorker): tối đa 4 request đồng thời, tối đa 10 request/giây, timeout 8s.
/// - Transaction terminal (Completed/Failed/Cancelled/Timeout/404) gỡ khỏi danh sách poll ngay, không poll lại.
/// - Chỉ dùng GET /workflows/transactions/{id} để lấy trạng thái; không dùng fleet/AGV để suy ra transaction.
/// - 429/5xx/timeout/mạng: backoff 1s→2s→…→60s + jitter, tôn trọng Retry-After. 401/403: dừng, chờ cấu hình mới.
/// - Log/event chỉ khi trạng thái đổi hoặc lỗi; đếm request/phút, lỗi, transaction active.
/// </summary>
public sealed class PumpService
{
    public const string ApiKeyHeader = "X-ACS-API-Key";
    private const string IntegrationApiPath = "/api/integration/v1";
    private const int MaxEvents = 50;

    // Chu kỳ tối thiểu: ACS cache trạng thái transaction 15s.
    public const int MinIntervalSeconds = 15;
    private const int DefaultIntervalSeconds = 15;
    private const int DefaultJitterPercent = 20;
    private const int DefaultMaxConcurrentRequests = 4;
    private const int DefaultMaxRequestsPerSecond = 10;
    private const int DefaultWarnRequestsPerMinute = 500;
    private const int BackoffBaseMs = 1000;
    private const double BackoffFactor = 2.0;
    private const int BackoffMaxMs = 60000;
    private const int AuthHoldSeconds = 60;
    private const int DisabledIdleSeconds = 5;

    private static readonly string[] TerminalStateNames =
        ["Completed", "Failed", "Cancelled", "Canceled", "Timeout", "NotFound"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PumpService> _logger;
    private readonly SimulatorConfig _simulatorConfig;
    private readonly IConfiguration _configuration;
    private readonly string _stateFile;
    private readonly object _gate = new();
    private CancellationTokenSource _wakeup = new();

    private string? _baseUrlOverride;
    private string _apiKey = "";
    private int _intervalSeconds = DefaultIntervalSeconds;
    private int _jitterPercent = DefaultJitterPercent;
    private int _maxConcurrentRequests = DefaultMaxConcurrentRequests;
    private int _maxRequestsPerSecond = DefaultMaxRequestsPerSecond;
    private int _warnRequestsPerMinute = DefaultWarnRequestsPerMinute;
    private int _targetRunning = 5;
    private int _lastPickedId;
    private bool _enabled;
    private List<int> _workflowIds = [];
    private readonly Dictionary<int, WorkflowRuntime> _runtimes = new();
    /// <summary>StartedCount cao nhất của workflow đã bị gỡ (giữ correlationId đơn điệu, tránh trùng sau restart).</summary>
    private readonly Dictionary<int, int> _retiredSeq = new();
    private readonly List<string> _events = [];
    private bool _acsReachable;
    private string? _acsVersion;
    private DateTime? _lastTickAtUtc;
    private DateTime? _nextTickAtUtc;

    // Backoff / auth / skip: chỉ tính trong bộ nhớ, không persist.
    private int _consecutiveFailures;
    private DateTime _backoffUntilUtc = DateTime.MinValue;
    private string? _authError;
    private string? _lastSkipReason;
    private bool _rateWarned;

    // Giới hạn tải + metric.
    private readonly object _throttleGate = new();
    private readonly Queue<DateTime> _secondWindow = new();
    private readonly Queue<DateTime> _requestTimes = new();
    private readonly Queue<DateTime> _errorTimes = new();

    public PumpService(
        IHttpClientFactory httpClientFactory,
        ILogger<PumpService> logger,
        SimulatorConfig simulatorConfig,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _simulatorConfig = simulatorConfig;
        _configuration = configuration;
        _stateFile = Path.Combine(AppContext.BaseDirectory, "data", "pump-state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
        Load();
    }

    /// <summary>BaseUrl override nếu user set; mặc định theo ACS API của simulator.</summary>
    private string EffectiveBaseUrl =>
        !string.IsNullOrWhiteSpace(_baseUrlOverride) ? _baseUrlOverride : _simulatorConfig.AcsApiBaseUrl;

    /// <summary>
    /// Key hiệu lực (không bao giờ log giá trị): ưu tiên secret/env, rồi config, cuối cùng key nhập tay qua UI.
    /// Env: ACS_PUMP_API_KEY (ưu tiên) hoặc ACS_API_KEY; config: Simulator:PumpApiKey (có thể set qua env Simulator__PumpApiKey).
    /// </summary>
    private (string BaseUrl, string ApiKey, string KeySource) GetConnection()
    {
        string? envKey = null;
        try
        {
            envKey = Environment.GetEnvironmentVariable("ACS_PUMP_API_KEY")
                ?? Environment.GetEnvironmentVariable("ACS_API_KEY");
        }
        catch { }
        string? cfgKey = null;
        try { cfgKey = _configuration["Simulator:PumpApiKey"]; } catch { }
        lock (_gate)
        {
            var baseUrl = EffectiveBaseUrl;
            if (!string.IsNullOrWhiteSpace(envKey))
                return (baseUrl, envKey.Trim(), "env");
            if (!string.IsNullOrWhiteSpace(cfgKey))
                return (baseUrl, cfgKey.Trim(), "config");
            return (baseUrl, _apiKey, string.IsNullOrWhiteSpace(_apiKey) ? "none" : "ui");
        }
    }

    public PumpSnapshot GetSnapshot()
    {
        int intervalSec, jitter, maxConc, maxRps, warnRpm, target;
        bool enabled, reachable;
        string? version, authError;
        DateTime? lastTick, nextTick;
        List<int> ids;
        List<WorkflowRuntime> runtimes;
        List<string> events;
        string baseUrl, keySource;
        bool hasKey;
        lock (_gate)
        {
            enabled = _enabled;
            reachable = _acsReachable;
            version = _acsVersion;
            authError = _authError;
            lastTick = _lastTickAtUtc;
            nextTick = _nextTickAtUtc;
            intervalSec = _intervalSeconds;
            jitter = _jitterPercent;
            maxConc = _maxConcurrentRequests;
            maxRps = _maxRequestsPerSecond;
            warnRpm = _warnRequestsPerMinute;
            target = _targetRunning;
            ids = [.. _workflowIds];
            runtimes = _runtimes.Values.OrderBy(r => r.WorkflowId).ToList();
            events = [.. _events];
            baseUrl = EffectiveBaseUrl;
        }
        var (connBaseUrl, connKey, source) = GetConnection();
        baseUrl = connBaseUrl;
        hasKey = !string.IsNullOrWhiteSpace(connKey);
        keySource = source;

        int reqMin, errMin, backoffMs;
        lock (_throttleGate)
        {
            PruneMinuteLocked(DateTime.UtcNow);
            reqMin = _requestTimes.Count;
            errMin = _errorTimes.Count;
        }
        lock (_gate)
        {
            backoffMs = _backoffUntilUtc > DateTime.UtcNow
                ? (int)(_backoffUntilUtc - DateTime.UtcNow).TotalMilliseconds
                : 0;
        }
        var active = runtimes.Count(r => r.LastTransactionId > 0 && !IsTerminal(r.LastState, r.LastStateCode));
        var busy = runtimes.Count(IsBusy);
        return new PumpSnapshot(
            enabled,
            baseUrl,
            hasKey,
            intervalSec,
            target,
            busy,
            ids,
            runtimes,
            events,
            reachable,
            version,
            lastTick,
            nextTick,
            DateTime.UtcNow,
            intervalSec * 1000,
            jitter,
            maxConc,
            maxRps,
            reqMin,
            errMin,
            active,
            keySource,
            authError,
            reqMin > warnRpm,
            backoffMs);
    }

    /// <summary>Cập nhật config; field null giữ giá trị cũ (key rỗng = không đổi key).</summary>
    public void UpdateConfig(PumpConfigRequest request)
    {
        lock (_gate)
        {
            var keyChanged = false;
            if (request.BaseUrl is not null)
            {
                var trimmed = request.BaseUrl.Trim().TrimEnd('/');
                var next = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
                if (_baseUrlOverride != next)
                {
                    _baseUrlOverride = next;
                    keyChanged = true;
                }
            }
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                _apiKey = request.ApiKey.Trim();
                keyChanged = true;
            }
            if (request.IntervalSeconds is int seconds)
            {
                _intervalSeconds = Math.Clamp(seconds, MinIntervalSeconds, 3600);
                if (_enabled)
                    _nextTickAtUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(ApplyJitter(_intervalSeconds * 1000, _jitterPercent));
            }
            if (request.JitterPercent is int jitter)
                _jitterPercent = Math.Clamp(jitter, 0, 50);
            if (request.MaxConcurrentRequests is int conc)
                _maxConcurrentRequests = Math.Clamp(conc, 1, DefaultMaxConcurrentRequests);
            if (request.MaxRequestsPerSecond is int rps)
                _maxRequestsPerSecond = Math.Clamp(rps, 1, DefaultMaxRequestsPerSecond);
            if (request.WarnRequestsPerMinute is int warn)
                _warnRequestsPerMinute = Math.Clamp(warn, 60, 6000);
            if (request.TargetRunning is int target)
                _targetRunning = Math.Clamp(target, 0, 1000);
            if (request.WorkflowIds is not null)
            {
                _workflowIds = request.WorkflowIds.Distinct().OrderBy(id => id).ToList();
                foreach (var staleId in _runtimes.Keys.Where(id => !_workflowIds.Contains(id)).ToList())
                {
                    // Giữ lại StartedCount để correlationId không tái sử dụng nếu workflow được thêm lại.
                    if (_runtimes.TryGetValue(staleId, out var stale))
                        _retiredSeq[staleId] = Math.Max(_retiredSeq.TryGetValue(staleId, out var s) ? s : 0, stale.StartedCount);
                    _runtimes.Remove(staleId);
                }
            }
            if (keyChanged)
            {
                // Cấu hình mới (key/baseUrl) → cho phép thử lại sau 401/403.
                _authError = null;
                _consecutiveFailures = 0;
                _backoffUntilUtc = DateTime.MinValue;
                _lastSkipReason = null;
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
            _lastSkipReason = null;
            KickLocked();
            Save();
        }
    }

    /// <summary>Worker gọi sau mỗi vòng để UI đếm ngược tới lần gọi tiếp theo.</summary>
    public void SetNextTick(DateTime nextTickAtUtc)
    {
        lock (_gate) _nextTickAtUtc = nextTickAtUtc;
    }

    /// <summary>Delay tới tick kế: interval + jitter, không ngắn hơn backoff còn lại.</summary>
    public TimeSpan GetDelay()
    {
        lock (_gate)
        {
            if (!_enabled)
                return TimeSpan.FromSeconds(DisabledIdleSeconds);
            if (_authError is not null)
                return TimeSpan.FromSeconds(AuthHoldSeconds);
            if (_nextTickAtUtc.HasValue)
            {
                var wait = _nextTickAtUtc.Value - DateTime.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
            }
            return TimeSpan.FromMilliseconds(ApplyJitter(_intervalSeconds * 1000, _jitterPercent));
        }
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

    /// <summary>Lấy danh sách workflow active từ ACS bằng key đã lưu (user bấm tay, vẫn tính vào giới hạn tải).</summary>
    public async Task<List<WorkflowSummary>> ListWorkflowsAsync(CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey, _) = GetConnection();
        var client = _httpClientFactory.CreateClient("PumpAcs");
        await WaitForRateSlotAsync(cancellationToken);
        RecordRequest();
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/workflows", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            NoteRetryable(null, "list workflows timeout");
            throw new InvalidOperationException("ACS request timeout, thử lại sau");
        }
        catch (HttpRequestException ex)
        {
            NoteRetryable(null, $"list workflows: {Shorten(ex.Message)}");
            throw new InvalidOperationException($"Không kết nối được ACS: {Shorten(ex.Message)}");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                NoteAuth($"list workflows {(int)response.StatusCode}");
                throw new InvalidOperationException("API key không hợp lệ (401/403). Cập nhật key mới.");
            }
            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
            {
                NoteRetryable(GetRetryAfterMs(response), $"list workflows {(int)response.StatusCode}");
                throw new InvalidOperationException($"ACS quá tải ({(int)response.StatusCode}), thử lại sau");
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                RecordError();
                throw new InvalidOperationException($"ACS returned {(int)response.StatusCode}: {Shorten(body)}");
            }
            var workflows = new List<WorkflowSummary>();
            foreach (var item in JsonDocument.Parse(body).RootElement.EnumerateArray())
            {
                if (TryGetProperty(item, "id", out var id) && TryGetProperty(item, "name", out var name))
                    workflows.Add(new WorkflowSummary(id.GetInt32(), name.GetString() ?? ""));
            }
            return workflows;
        }
    }

    /// <summary>
    /// Một tick của vòng poll duy nhất: health-check, poll các transaction tới hạn (tối đa 4 song song),
    /// chưa đủ TargetRunning thì start đúng 1 workflow rảnh tiếp theo theo vòng tròn.
    /// Không log mỗi lần poll — chỉ event khi trạng thái đổi hoặc lỗi.
    /// </summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _lastTickAtUtc = DateTime.UtcNow;
        var (baseUrl, apiKey, _) = GetConnection();
        var ids = GetWorkflowIds();
        int intervalSec, jitterPct, maxConc;
        lock (_gate)
        {
            intervalSec = _intervalSeconds;
            jitterPct = _jitterPercent;
            maxConc = _maxConcurrentRequests;
            if (_authError is not null)
            {
                NoteSkippedLocked("auth");
                ScheduleNextLocked();
                return;
            }
            if (_backoffUntilUtc > DateTime.UtcNow)
            {
                NoteSkippedLocked("backoff");
                ScheduleNextLocked();
                return;
            }
        }
        if (ids.Count == 0 || string.IsNullOrWhiteSpace(apiKey))
        {
            lock (_gate)
            {
                NoteSkippedLocked(ids.Count == 0 ? "no-workflows" : "no-key");
                ScheduleNextLocked();
            }
            return;
        }

        var client = _httpClientFactory.CreateClient("PumpAcs");
        try
        {
            if (!await CheckAcsHealthAsync(client, baseUrl, apiKey, cancellationToken))
            {
                lock (_gate)
                {
                    NoteSkippedLocked("unreachable");
                    ScheduleNextLocked();
                }
                Save();
                return;
            }
        }
        catch (PumpAuthException ex)
        {
            NoteAuth(ex.Message);
            lock (_gate) ScheduleNextLocked();
            return;
        }
        catch (PumpRetryableException ex)
        {
            NoteRetryable(ex.RetryAfterMs, "health");
            lock (_gate) ScheduleNextLocked();
            return;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            NoteRetryable(null, $"health: {Shorten(ex.Message)}");
            lock (_gate) ScheduleNextLocked();
            return;
        }

        // Phân loại theo trạng thái đã biết (không request): terminal/không tx = rảnh;
        // chưa tới hạn poll lại thì giữ phân loại cũ để không tạo tải vô ích.
        var now = DateTime.UtcNow;
        var dueIds = new List<int>();
        var free = new List<int>();
        var active = 0;
        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (!_runtimes.TryGetValue(id, out var r) || r.LastTransactionId <= 0)
                {
                    free.Add(id);
                    continue;
                }
                if (IsTerminal(r.LastState, r.LastStateCode))
                {
                    free.Add(id);
                    continue;
                }
                if (r.NextPollAtUtc.HasValue && now < r.NextPollAtUtc.Value)
                {
                    // Chưa tới hạn 15s → giữ nguyên phân loại cũ, không poll lại.
                    // Tx chưa rõ trạng thái vẫn chiếm slot (không start đè lên workflow này).
                    active++;
                    continue;
                }
                dueIds.Add(id);
            }
        }

        // Poll các transaction tới hạn — tối đa maxConc request đồng thời.
        var outcome = new TickOutcome();
        if (dueIds.Count > 0)
        {
            using var sem = new SemaphoreSlim(Math.Max(1, maxConc), Math.Max(1, maxConc));
            var checks = dueIds.Select(workflowId =>
                CheckOneAsync(client, baseUrl, apiKey, workflowId, intervalSec, jitterPct, sem, outcome, cancellationToken));
            await Task.WhenAll(checks);
            if (outcome.Auth is not null)
            {
                NoteAuth(outcome.Auth);
                lock (_gate) ScheduleNextLocked();
                return;
            }
            if (outcome.Retryable > 0)
                NoteRetryable(outcome.RetryAfterMs, $"{outcome.Retryable} tx check");
            else
                ResetBackoff();
        }
        else
        {
            ResetBackoff();
        }

        // Tính lại rảnh/bận theo trạng thái mới nhất rồi mới quyết định start.
        List<int> freeNow;
        int activeNow;
        int target;
        lock (_gate)
        {
            target = _targetRunning;
            freeNow = [];
            activeNow = 0;
            foreach (var id in ids)
            {
                if (!_runtimes.TryGetValue(id, out var r) || r.LastTransactionId <= 0)
                {
                    freeNow.Add(id);
                    continue;
                }
                if (IsTerminal(r.LastState, r.LastStateCode))
                    freeNow.Add(id);
                else
                    activeNow++;
            }
        }

        if (target > 0 && activeNow < target && freeNow.Count > 0)
        {
            var nextId = PickNextRoundRobin(ids, freeNow);
            if (nextId.HasValue)
                await StartPickedAsync(client, baseUrl, apiKey, nextId.Value, cancellationToken);
        }
        // Ổn định (đủ target / không còn workflow rảnh): không event, không log — tránh spam.

        CheckRateWarning();
        lock (_gate)
        {
            _lastSkipReason = null;
            ScheduleNextLocked();
        }
        Save();
    }

    /// <summary>Hủy 1 transaction đang chạy trên ACS (dùng từ UI). 404 = tx đã hết, gỡ khỏi poll ngay.</summary>
    public async Task<string> CancelTransactionAsync(long transactionId, CancellationToken cancellationToken)
    {
        var (baseUrl, apiKey, _) = GetConnection();
        var client = _httpClientFactory.CreateClient("PumpAcs");
        await WaitForRateSlotAsync(cancellationToken);
        RecordRequest();
        using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{IntegrationApiPath}/workflows/transactions/{transactionId}/cancel", apiKey);
        request.Content = new StringContent("{\"reason\":\"Cancelled from simulator pump\"}", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            NoteRetryable(null, "cancel timeout");
            throw new InvalidOperationException("ACS request timeout, thử lại sau");
        }
        catch (HttpRequestException ex)
        {
            NoteRetryable(null, $"cancel: {Shorten(ex.Message)}");
            throw new InvalidOperationException($"Không kết nối được ACS: {Shorten(ex.Message)}");
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                MarkTransactionTerminal(transactionId, "Cancelled");
                return body;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                NoteAuth($"cancel {(int)response.StatusCode}");
                throw new InvalidOperationException("API key không hợp lệ (401/403). Cập nhật key mới.");
            }
            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
            {
                NoteRetryable(GetRetryAfterMs(response), $"cancel {(int)response.StatusCode}");
                throw new InvalidOperationException($"ACS quá tải ({(int)response.StatusCode}), thử lại sau");
            }
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Accepted)
            {
                RecordError();
                throw new InvalidOperationException($"cancel failed {(int)response.StatusCode}: {Shorten(body)}");
            }
            MarkTransactionTerminal(transactionId, "Cancelled");
            _logger.LogInformation("Pump cancelled transaction {TransactionId}", transactionId);
            return body;
        }
    }

    private async Task<bool> CheckAcsHealthAsync(HttpClient client, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        await WaitForRateSlotAsync(cancellationToken);
        RecordRequest();
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/health", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PumpRetryableException("ACS health timeout", null);
        }
        catch (HttpRequestException ex)
        {
            throw new PumpRetryableException($"ACS health: {Shorten(ex.Message)}", null);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new PumpAuthException($"health {(int)response.StatusCode}");
            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                throw new PumpRetryableException($"health {(int)response.StatusCode}", GetRetryAfterMs(response));
            var reachable = response.IsSuccessStatusCode;
            string? version = null;
            if (reachable)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    if (TryGetProperty(JsonDocument.Parse(body).RootElement, "version", out var v))
                        version = v.GetString();
                }
                catch { }
            }
            bool changed;
            lock (_gate)
            {
                changed = _acsReachable != reachable;
                _acsReachable = reachable;
                _acsVersion = version;
                if (!reachable)
                    UpdateAllRuntimeNotesLocked("ACS không kết nối được");
            }
            if (changed)
                AddEvent(reachable ? "ACS reachable: ON" : "ACS không kết nối được — chờ tick sau");
            return reachable;
        }
    }

    private sealed class TickOutcome
    {
        public int Success;
        public int Retryable;
        public int? RetryAfterMs;
        public string? Auth;
    }

    private async Task CheckOneAsync(
        HttpClient client,
        string baseUrl,
        string apiKey,
        int workflowId,
        int intervalSec,
        int jitterPct,
        SemaphoreSlim sem,
        TickOutcome outcome,
        CancellationToken cancellationToken)
    {
        await sem.WaitAsync(cancellationToken);
        try
        {
            await WaitForRateSlotAsync(cancellationToken);
            long txId;
            string? oldState;
            int? oldCode;
            lock (_gate)
            {
                if (!_runtimes.TryGetValue(workflowId, out var existing))
                    return;
                txId = existing.LastTransactionId;
                oldState = existing.LastState;
                oldCode = existing.LastStateCode;
            }
            if (txId <= 0)
                return;
            RecordRequest();
            TxInfo tx;
            try
            {
                tx = await GetTransactionAsync(client, baseUrl, apiKey, txId, cancellationToken);
            }
            catch (PumpNotFoundException)
            {
                // 404: transaction không còn → gỡ khỏi danh sách poll ngay.
                UpdateRuntime(workflowId, r =>
                {
                    r.LastState = "NotFound";
                    r.LastStateCode = null;
                    r.PendingReason = null;
                    r.NextPollAtUtc = null;
                    r.LastCheckedAt = DateTime.UtcNow;
                    r.Note = null;
                });
                OnStateChanged(workflowId, txId, oldState, oldCode, "NotFound", null);
                return;
            }
            catch (PumpAuthException ex)
            {
                lock (outcome) outcome.Auth ??= ex.Message;
                return;
            }
            catch (PumpRetryableException ex)
            {
                lock (outcome)
                {
                    outcome.Retryable++;
                    if (ex.RetryAfterMs.HasValue)
                        outcome.RetryAfterMs = Math.Max(outcome.RetryAfterMs ?? 0, ex.RetryAfterMs.Value);
                }
                UpdateRuntime(workflowId, r => r.Note = Shorten(ex.Message));
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                RecordError();
                SetNoteIfChanged(workflowId, Shorten(ex.Message));
                return;
            }

            lock (outcome) outcome.Success++;
            var terminal = IsTerminal(tx.State, tx.StateCode);
            var now = DateTime.UtcNow;
            UpdateRuntime(workflowId, r =>
            {
                r.LastState = tx.State;
                r.LastStateCode = tx.StateCode;
                r.PendingReason = tx.PendingReason;
                r.AgvName = tx.AgvName;
                r.CommandAccepted = tx.CommandAccepted;
                r.LastCheckedAt = now;
                // Terminal: gỡ khỏi poll (không đặt NextPollAt); active: hẹn lần poll kế sau 15s + jitter.
                r.NextPollAtUtc = terminal ? null : now + TimeSpan.FromMilliseconds(ApplyJitter(intervalSec * 1000, jitterPct));
                r.Note = null;
            });
            if (!string.Equals(oldState, tx.State, StringComparison.Ordinal) || oldCode != tx.StateCode)
                OnStateChanged(workflowId, txId, oldState, oldCode, tx.State, tx.StateCode);
        }
        finally
        {
            sem.Release();
        }
    }

    private void OnStateChanged(int workflowId, long txId, string? oldState, int? oldCode, string? newState, int? newCode)
    {
        AddEvent($"wf {workflowId} tx {txId}: {FormatState(oldState, oldCode)} -> {FormatState(newState, newCode)}");
        _logger.LogInformation("Pump wf {WorkflowId} tx {TransactionId}: {Old} -> {New}",
            workflowId, txId, FormatState(oldState, oldCode), FormatState(newState, newCode));
    }

    private static string FormatState(string? state, int? code)
        => $"{state ?? "?"}#{code?.ToString() ?? "?"}";

    private async Task StartPickedAsync(HttpClient client, string baseUrl, string apiKey, int workflowId, CancellationToken cancellationToken)
    {
        // correlationId ổn định theo seq: retry lần sau dùng đúng correlationId cũ → ACS dedupe, không tạo trùng.
        var seq = GetStartedCount(workflowId) + 1;
        var correlationId = $"pump-{workflowId}-{seq}";
        await WaitForRateSlotAsync(cancellationToken);
        RecordRequest();
        StartedWorkflow started;
        try
        {
            started = await StartWorkflowAsync(client, baseUrl, apiKey, workflowId, correlationId, cancellationToken);
        }
        catch (PumpAuthException ex)
        {
            NoteAuth(ex.Message);
            return;
        }
        catch (PumpRetryableException ex)
        {
            // Giữ nguyên StartedCount → lần sau retry cùng correlationId (idempotent).
            NoteRetryable(ex.RetryAfterMs, "start");
            UpdateRuntime(workflowId, r => r.Note = Shorten(ex.Message));
            return;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            RecordError();
            SetNoteIfChanged(workflowId, Shorten(ex.Message));
            AddEventIfChanged($"wf {workflowId} error: {Shorten(ex.Message)}", $"wf-error-{workflowId}");
            return;
        }
        int intervalSec, jitterPct;
        lock (_gate)
        {
            intervalSec = _intervalSeconds;
            jitterPct = _jitterPercent;
        }
        var now = DateTime.UtcNow;
        UpdateRuntime(workflowId, r =>
        {
            r.LastTransactionId = started.TransactionId;
            r.LastState = started.Status;
            r.LastStateCode = null;
            r.PendingReason = null;
            r.AgvName = null;
            r.CommandAccepted = null;
            r.LastCheckedAt = now;
            r.NextPollAtUtc = IsTerminal(started.Status, null)
                ? null
                : now + TimeSpan.FromMilliseconds(ApplyJitter(intervalSec * 1000, jitterPct));
            r.StartedCount++;
            r.Note = null;
        });
        AddEvent($"wf {workflowId} start -> tx {started.TransactionId} ({started.Status})");
        _logger.LogInformation("Pump wf {WorkflowId} start -> tx {TransactionId} ({Status})",
            workflowId, started.TransactionId, started.Status);
    }

    private void MarkTransactionTerminal(long transactionId, string state)
    {
        lock (_gate)
        {
            foreach (var r in _runtimes.Values.Where(r => r.LastTransactionId == transactionId))
            {
                var old = r.LastState;
                var oldCode = r.LastStateCode;
                r.LastState = state;
                r.LastStateCode = null;
                r.NextPollAtUtc = null;
                r.LastCheckedAt = DateTime.UtcNow;
                r.Note = null;
                if (!string.Equals(old, state, StringComparison.Ordinal))
                    OnStateChanged(r.WorkflowId, transactionId, old, oldCode, state, null);
                else
                    AddEvent($"tx {transactionId} {state}");
            }
        }
    }

    private int GetStartedCount(int workflowId)
    {
        lock (_gate)
        {
            var live = _runtimes.TryGetValue(workflowId, out var r) ? r.StartedCount : 0;
            var retired = _retiredSeq.TryGetValue(workflowId, out var s) ? s : 0;
            return Math.Max(live, retired);
        }
    }

    private sealed record TxInfo(string? State, int? StateCode, string? PendingReason, string? AgvName, bool? CommandAccepted, bool Busy);

    // GET trạng thái transaction — endpoint duy nhất dùng để poll (không dùng fleet/AGV).
    // 404 coi như đã kết thúc → gỡ khỏi poll, tick sẽ start bù nếu còn thiếu target.
    private static async Task<TxInfo> GetTransactionAsync(
        HttpClient client, string baseUrl, string apiKey, long transactionId, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl}{IntegrationApiPath}/workflows/transactions/{transactionId}", apiKey);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PumpRetryableException($"check tx {transactionId} timeout", null);
        }
        catch (HttpRequestException ex)
        {
            throw new PumpRetryableException($"check tx {transactionId}: {Shorten(ex.Message)}", null);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new PumpNotFoundException();
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new PumpAuthException($"check tx {transactionId} {(int)response.StatusCode}");
            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                throw new PumpRetryableException($"check tx {transactionId} {(int)response.StatusCode}", GetRetryAfterMs(response));
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
    }

    // POST start 1 instance mới, không gán AGV; correlationId giúp retry idempotent (ACS dedupe).
    private static async Task<StartedWorkflow> StartWorkflowAsync(
        HttpClient client, string baseUrl, string apiKey, int workflowId, string correlationId, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, $"{baseUrl}{IntegrationApiPath}/workflows/{workflowId}/start", apiKey);
        request.Content = new StringContent($"{{\"correlationId\":\"{correlationId}\"}}", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PumpRetryableException($"start wf {workflowId} timeout", null);
        }
        catch (HttpRequestException ex)
        {
            throw new PumpRetryableException($"start wf {workflowId}: {Shorten(ex.Message)}", null);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                throw new PumpAuthException($"start wf {workflowId} {(int)response.StatusCode}");
            if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                throw new PumpRetryableException($"start wf {workflowId} {(int)response.StatusCode}", GetRetryAfterMs(response));
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"start failed {(int)response.StatusCode}: {Shorten(body)}");
            var json = JsonDocument.Parse(body).RootElement;
            var txId = TryGetProperty(json, "transactionId", out var tx) ? tx.GetInt64() : 0L;
            var status = TryGetProperty(json, "status", out var st) ? st.GetString() ?? "?" : "?";
            return new StartedWorkflow(txId, status);
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        return request;
    }

    private static bool IsBusy(WorkflowRuntime r)
        => r.LastStateCode is 1 or 2 || r.LastState is "Pending" or "Created" or "Running";

    /// <summary>
    /// Terminal: chuỗi Completed/Failed/Cancelled(+biến thể)/Timeout/NotFound (không phân biệt hoa thường),
    /// hoặc stateCode số khác 1 (Pending) và 2 (Running). Không rõ trạng thái (null/?) = còn active, vẫn poll.
    /// </summary>
    private static bool IsTerminal(string? state, int? stateCode)
    {
        if (state is not null)
        {
            foreach (var t in TerminalStateNames)
            {
                if (string.Equals(state, t, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        if (stateCode is 1 or 2)
            return false;
        if (stateCode.HasValue)
            return true;
        return false;
    }

    private static int ApplyJitter(int baseMs, int percent)
    {
        if (percent <= 0)
            return Math.Max(0, baseMs);
        var p = Math.Clamp(percent, 0, 50) / 100.0;
        var factor = 1.0 + (Random.Shared.NextDouble() * 2.0 - 1.0) * p;
        return Math.Max(0, (int)(baseMs * factor));
    }

    private static int? GetRetryAfterMs(HttpResponseMessage response)
    {
        try
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
                return null;
            if (retryAfter.Delta.HasValue)
                return Math.Max(0, (int)retryAfter.Delta.Value.TotalMilliseconds);
            if (retryAfter.Date.HasValue)
            {
                var ms = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
                return ms > 0 ? (int)ms : 0;
            }
        }
        catch { }
        return null;
    }

    // ---- Backoff / auth / skip (ghi event 1 lần, không spam) ----

    private void NoteRetryable(int? retryAfterMs, string where)
    {
        int delay;
        int failures;
        lock (_gate)
        {
            _consecutiveFailures++;
            failures = _consecutiveFailures;
            var exp = (int)Math.Min(BackoffMaxMs, BackoffBaseMs * Math.Pow(BackoffFactor, failures - 1));
            delay = Math.Max(exp, retryAfterMs ?? 0);
            delay = Math.Min(BackoffMaxMs, delay);
            delay = ApplyJitter(delay, _jitterPercent);
            _backoffUntilUtc = DateTime.UtcNow.AddMilliseconds(delay);
        }
        RecordError();
        AddEvent($"ACS lỗi tạm ({where}) — backoff {delay / 1000.0:F1}s (lần {failures})");
        _logger.LogWarning("Pump backoff {DelayMs}ms after {Where} (failure {Failures})", delay, where, failures);
    }

    private void ResetBackoff()
    {
        bool wasFailing;
        lock (_gate)
        {
            wasFailing = _consecutiveFailures > 0 || _backoffUntilUtc > DateTime.UtcNow;
            _consecutiveFailures = 0;
            _backoffUntilUtc = DateTime.MinValue;
        }
        if (wasFailing)
            AddEvent("ACS ổn định trở lại, hết backoff");
    }

    private void NoteAuth(string message)
    {
        bool changed;
        lock (_gate)
        {
            changed = _authError != message;
            _authError = message;
        }
        RecordError();
        if (changed)
        {
            // Không log key; dừng poll, chờ cấu hình mới (UpdateConfig xóa lỗi này).
            AddEvent($"pump DỪNG: key/cấu hình không hợp lệ ({message}). Cập nhật key mới để chạy lại.");
            _logger.LogError("Pump stopped: invalid API key/configuration ({Reason}). Update key to resume.", message);
        }
    }

    private void NoteSkippedLocked(string reason)
    {
        if (_lastSkipReason == reason)
            return;
        _lastSkipReason = reason;
        AddEventLocked(reason switch
        {
            "no-workflows" => "tick: chưa chọn workflow nào, bỏ qua",
            "no-key" => "tick: thiếu API key (đặt ACS_PUMP_API_KEY hoặc nhập key trong Settings), bỏ qua",
            "auth" => "tick: đang dừng do key không hợp lệ, chờ cấu hình mới",
            "backoff" => "tick: đang backoff, bỏ qua",
            "unreachable" => "tick: ACS không kết nối được, bỏ qua",
            _ => $"tick: bỏ qua ({reason})"
        });
    }

    private void ScheduleNextLocked()
    {
        var intervalMs = ApplyJitter(_intervalSeconds * 1000, _jitterPercent);
        var backoffMs = _backoffUntilUtc > DateTime.UtcNow
            ? (int)(_backoffUntilUtc - DateTime.UtcNow).TotalMilliseconds
            : 0;
        _nextTickAtUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(intervalMs, backoffMs));
    }

    private void CheckRateWarning()
    {
        int reqMin, warn;
        lock (_throttleGate)
        {
            PruneMinuteLocked(DateTime.UtcNow);
            reqMin = _requestTimes.Count;
        }
        lock (_gate) warn = _warnRequestsPerMinute;
        bool over = reqMin > warn;
        bool changed;
        lock (_gate)
        {
            changed = over && !_rateWarned;
            if (!over)
                _rateWarned = false;
            else if (changed)
                _rateWarned = true;
        }
        if (changed)
            AddEvent($"cảnh báo: {reqMin} request/phút vượt ngưỡng {warn}");
    }

    // ---- Giới hạn tải + metric (không khóa _gate khi chờ) ----

    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        int maxRps;
        lock (_gate) maxRps = _maxRequestsPerSecond;
        while (true)
        {
            TimeSpan wait;
            lock (_throttleGate)
            {
                var now = DateTime.UtcNow;
                while (_secondWindow.Count > 0 && (now - _secondWindow.Peek()).TotalSeconds >= 1)
                    _secondWindow.Dequeue();
                if (_secondWindow.Count < Math.Max(1, maxRps))
                {
                    _secondWindow.Enqueue(now);
                    return;
                }
                wait = _secondWindow.Peek().AddSeconds(1) - now;
                if (wait < TimeSpan.Zero)
                    wait = TimeSpan.Zero;
            }
            await Task.Delay(wait, cancellationToken);
        }
    }

    private void RecordRequest()
    {
        lock (_throttleGate)
        {
            var now = DateTime.UtcNow;
            _requestTimes.Enqueue(now);
            PruneMinuteLocked(now);
        }
    }

    private void RecordError()
    {
        lock (_throttleGate)
        {
            var now = DateTime.UtcNow;
            _errorTimes.Enqueue(now);
            PruneMinuteLocked(now);
        }
    }

    private void PruneMinuteLocked(DateTime now)
    {
        while (_requestTimes.Count > 0 && (now - _requestTimes.Peek()).TotalMinutes >= 1)
            _requestTimes.Dequeue();
        while (_errorTimes.Count > 0 && (now - _errorTimes.Peek()).TotalMinutes >= 1)
            _errorTimes.Dequeue();
    }

    // ---- Runtime helpers ----

    private void UpdateAllRuntimeNotesLocked(string note)
    {
        foreach (var r in _runtimes.Values)
            r.Note = note;
    }

    private void SetNoteIfChanged(int workflowId, string note)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_runtimes.TryGetValue(workflowId, out var runtime))
            {
                runtime = new WorkflowRuntime { WorkflowId = workflowId };
                _runtimes[workflowId] = runtime;
            }
            if (runtime.Note != note)
            {
                runtime.Note = note;
                changed = true;
            }
        }
        if (changed)
            AddEvent($"wf {workflowId} lỗi: {note}");
    }

    private readonly Dictionary<string, string> _lastChangedEvents = new();

    private void AddEventIfChanged(string message, string dedupeKey)
    {
        lock (_gate)
        {
            if (_lastChangedEvents.TryGetValue(dedupeKey, out var last) && last == message)
                return;
            _lastChangedEvents[dedupeKey] = message;
            if (_lastChangedEvents.Count > 200)
                _lastChangedEvents.Clear();
            AddEventLocked(message);
        }
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

    private void AddEvent(string message)
    {
        lock (_gate) AddEventLocked(message);
    }

    private void AddEventLocked(string message)
    {
        _events.Insert(0, $"{DateTime.Now:HH:mm:ss} {message}");
        if (_events.Count > MaxEvents)
            _events.RemoveAt(_events.Count - MaxEvents);
    }

    private void Save()
    {
        int lastPicked, intervalSec, jitter, maxConc, maxRps, warnRpm, target;
        string? baseUrl;
        string apiKey;
        bool enabled;
        List<int> ids;
        Dictionary<int, WorkflowRuntime> runtimes;
        Dictionary<int, int> retired;
        lock (_gate)
        {
            lastPicked = _lastPickedId;
            intervalSec = _intervalSeconds;
            jitter = _jitterPercent;
            maxConc = _maxConcurrentRequests;
            maxRps = _maxRequestsPerSecond;
            warnRpm = _warnRequestsPerMinute;
            target = _targetRunning;
            baseUrl = _baseUrlOverride;
            apiKey = _apiKey;
            enabled = _enabled;
            ids = _workflowIds;
            runtimes = new Dictionary<int, WorkflowRuntime>(_runtimes);
            retired = new Dictionary<int, int>(_retiredSeq);
        }
        var state = new PersistedState
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            IntervalSeconds = intervalSec,
            JitterPercent = jitter,
            MaxConcurrentRequests = maxConc,
            MaxRequestsPerSecond = maxRps,
            WarnRequestsPerMinute = warnRpm,
            TargetRunning = target,
            Enabled = enabled,
            WorkflowIds = ids,
            LastPickedId = lastPicked,
            Runtimes = runtimes,
            RetiredSeq = retired
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
            _intervalSeconds = Math.Clamp(state.IntervalSeconds, MinIntervalSeconds, 3600);
            _jitterPercent = state.JitterPercent is null ? DefaultJitterPercent : Math.Clamp(state.JitterPercent.Value, 0, 50);
            _maxConcurrentRequests = state.MaxConcurrentRequests is null
                ? DefaultMaxConcurrentRequests
                : Math.Clamp(state.MaxConcurrentRequests.Value, 1, DefaultMaxConcurrentRequests);
            _maxRequestsPerSecond = state.MaxRequestsPerSecond is null
                ? DefaultMaxRequestsPerSecond
                : Math.Clamp(state.MaxRequestsPerSecond.Value, 1, DefaultMaxRequestsPerSecond);
            _warnRequestsPerMinute = state.WarnRequestsPerMinute is null
                ? DefaultWarnRequestsPerMinute
                : Math.Clamp(state.WarnRequestsPerMinute.Value, 60, 6000);
            _targetRunning = Math.Clamp(state.TargetRunning, 0, 1000);
            _enabled = state.Enabled;
            _workflowIds = state.WorkflowIds ?? [];
            _lastPickedId = state.LastPickedId;
            foreach (var (id, seq) in state.RetiredSeq ?? [])
                _retiredSeq[id] = Math.Max(_retiredSeq.TryGetValue(id, out var cur) ? cur : 0, seq);
            // Sau restart chỉ resume transaction còn active; terminal thì gỡ khỏi poll ngay (giữ StartedCount).
            // Rải lần poll đầu sau restart trong 1 chu kỳ để tránh dồn request.
            var now = DateTime.UtcNow;
            foreach (var (id, runtime) in state.Runtimes ?? [])
            {
                if (_workflowIds.Count > 0 && !_workflowIds.Contains(id))
                {
                    _retiredSeq[id] = Math.Max(_retiredSeq.TryGetValue(id, out var rs) ? rs : 0, runtime.StartedCount);
                    continue;
                }
                if (IsTerminal(runtime.LastState, runtime.LastStateCode))
                {
                    _retiredSeq[id] = Math.Max(_retiredSeq.TryGetValue(id, out var ts) ? ts : 0, runtime.StartedCount);
                    continue;
                }
                runtime.WorkflowId = id;
                runtime.NextPollAtUtc = now + TimeSpan.FromMilliseconds(Random.Shared.Next(0, _intervalSeconds * 1000));
                _runtimes[id] = runtime;
            }
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

    private sealed class PumpRetryableException(string message, int? retryAfterMs) : Exception(message)
    {
        public int? RetryAfterMs { get; } = retryAfterMs;
    }

    private sealed class PumpAuthException(string message) : Exception(message);

    private sealed class PumpNotFoundException : Exception;

    // Schema khớp acs-pump.json cũ để file state migrate drop-in (field mới optional).
    private sealed class PersistedState
    {
        public string? BaseUrl { get; set; }
        public string ApiKey { get; set; } = "";
        public int IntervalSeconds { get; set; } = DefaultIntervalSeconds;
        public int? JitterPercent { get; set; }
        public int? MaxConcurrentRequests { get; set; }
        public int? MaxRequestsPerSecond { get; set; }
        public int? WarnRequestsPerMinute { get; set; }
        public int TargetRunning { get; set; } = 5;
        public bool Enabled { get; set; }
        public List<int> WorkflowIds { get; set; } = [];
        public int LastPickedId { get; set; }
        public Dictionary<int, WorkflowRuntime> Runtimes { get; set; } = new();
        public Dictionary<int, int> RetiredSeq { get; set; } = new();
    }
}

public sealed record StartedWorkflow(long TransactionId, string Status);
