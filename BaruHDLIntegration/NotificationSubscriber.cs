using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hdlctrl.V1;
using ResoniteModLoader;

namespace BaruHDLIntegration
{
    /// <summary>
    /// SubscribeNotifications ストリームを常時張り、イベントを配信する購読者。
    /// controller が非同期 job 化した StartWorld/StartHost の完了検出にも利用される。
    /// </summary>
    internal class NotificationSubscriber
    {
        // 待機登録より先に届いた完了通知を拾うための保持期間
        private static readonly TimeSpan RecentCompletionTtl = TimeSpan.FromMinutes(10);
        // stream 切断中の取りこぼし対策として GetAsyncJob でポーリングする間隔
        private static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(15);
        // ポーリングで完了を検出したとき、message を持つ完了通知の到着を待つ猶予
        private static readonly TimeSpan NotificationGracePeriod = TimeSpan.FromSeconds(2);

        // START_SESSION はホスト起動 (停止中ホスト指定時) を含むので長めに待つ
        private static readonly TimeSpan StartSessionJobTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan BuildImageJobTimeout = TimeSpan.FromMinutes(60);
        // build → start_session の chain は最新版が更に出た場合に繰り返されうるので上限を設ける
        private const int MaxStartSessionChainHops = 3;

        // controller の chainBuild / buildImage が完了 message に埋め込む後続 job ID
        private static readonly Regex BuildJobIdPattern = new(@"build_job=([0-9A-Za-z-]+)", RegexOptions.Compiled);
        private static readonly Regex SessionStartJobIdPattern = new(@"session_start_job=([0-9A-Za-z-]+)", RegexOptions.Compiled);

        private readonly HDLControllerClient _client;
        private CancellationTokenSource? _cts;
        private volatile bool _started;
        private readonly object _startLock = new();

        // 完了待ちの job_id → PendingJob 情報
        private readonly ConcurrentDictionary<string, PendingJob> _pendingJobs = new();

        // 直近に受信した完了通知 (job_id → 結果)。StartWorld の応答から待機登録までの間に
        // job が完了してしまうケースの取りこぼし防止に使う。
        private readonly ConcurrentDictionary<string, RecentCompletion> _recentCompletions = new();

        internal NotificationSubscriber(HDLControllerClient client)
        {
            _client = client;
        }

        /// <summary>
        /// 購読ループを開始する (べき等)
        /// </summary>
        public void Start()
        {
            lock (_startLock)
            {
                if (_started) return;
                _started = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => RunLoopAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            lock (_startLock)
            {
                if (!_started) return;
                _started = false;
                _cts?.Cancel();
                _cts = null;
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            var backoffMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var ev in _client.NotificationService.SubscribeNotificationsAsync(
                        new SubscribeNotificationsRequest(), ct))
                    {
                        backoffMs = 1000; // 正常受信で backoff リセット
                        DispatchEvent(ev);
                    }
                    // 正常に stream が閉じたら短い待機で再接続
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"NotificationSubscriber: stream error, retry in {backoffMs}ms: {ex.Message}");
                    try
                    {
                        await Task.Delay(backoffMs, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    backoffMs = Math.Min(backoffMs * 2, 30_000);
                }
            }
        }

        private void DispatchEvent(NotificationEvent ev)
        {
            if (ev.JobCompleted is { } jc)
            {
                JobCompletion? completion = jc.Level switch
                {
                    JobCompletedEvent.Types.Level.Success => new JobCompletion(true, jc.Message),
                    JobCompletedEvent.Types.Level.Error => new JobCompletion(false, jc.Message),
                    _ => null,
                };
                if (completion == null) return;

                PruneRecentCompletions();
                _recentCompletions[jc.JobId] = new RecentCompletion(completion, DateTime.UtcNow);
                if (_pendingJobs.TryGetValue(jc.JobId, out var pending))
                {
                    pending.Tcs.TrySetResult(completion);
                }
            }
        }

        private void PruneRecentCompletions()
        {
            var threshold = DateTime.UtcNow - RecentCompletionTtl;
            foreach (var kv in _recentCompletions)
            {
                if (kv.Value.ReceivedAtUtc < threshold)
                {
                    _recentCompletions.TryRemove(kv.Key, out _);
                }
            }
        }

        /// <summary>
        /// 指定 job_id の完了を待つ。完了通知を主とし、stream 断に備えて GetAsyncJob のポーリングも併用する。
        /// タイムアウト or キャンセル時は null を返す。
        /// </summary>
        public async Task<JobCompletion?> WaitForJobAsync(string jobId, TimeSpan timeout, CancellationToken ct = default)
        {
            Start();

            var pending = new PendingJob();
            _pendingJobs[jobId] = pending;
            try
            {
                // 登録前に届いていた完了通知を拾う
                if (_recentCompletions.TryGetValue(jobId, out var recent))
                {
                    return recent.Completion;
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(timeout);
                var token = linkedCts.Token;
                try
                {
                    while (true)
                    {
                        var polled = await PollJobAsync(jobId, token).ConfigureAwait(false);
                        if (polled != null)
                        {
                            // 成功通知の message には chain 先の job ID が載るため、通知が来るなら優先する
                            await Task.WhenAny(pending.Tcs.Task, Task.Delay(NotificationGracePeriod, token)).ConfigureAwait(false);
                            return pending.Tcs.Task.IsCompleted ? await pending.Tcs.Task.ConfigureAwait(false) : polled;
                        }

                        var finished = await Task.WhenAny(pending.Tcs.Task, Task.Delay(JobPollInterval, token)).ConfigureAwait(false);
                        if (finished == pending.Tcs.Task)
                        {
                            return await pending.Tcs.Task.ConfigureAwait(false);
                        }
                        token.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            finally
            {
                _pendingJobs.TryRemove(jobId, out _);
            }
        }

        /// <summary>
        /// GetAsyncJob で job の状態を確認する。完了済みなら結果を、未完了や取得失敗時は null を返す。
        /// </summary>
        private async Task<JobCompletion?> PollJobAsync(string jobId, CancellationToken ct)
        {
            try
            {
                var res = await _client.GetAsyncJobAsync(new GetAsyncJobRequest { Id = jobId }, ct).ConfigureAwait(false);
                return res.Job?.Status switch
                {
                    AsyncJobStatus.Succeeded => new JobCompletion(true, ""),
                    AsyncJobStatus.Failed => new JobCompletion(false, res.Job.LastError ?? ""),
                    _ => null,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ResoniteMod.Warn($"NotificationSubscriber: failed to poll job {jobId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// StartWorld の job 完了を待ち、作成された session_id を返す。
        /// イメージ未ビルドで BUILD_IMAGE → START_SESSION が chain された場合はそれも辿る。
        /// </summary>
        public async Task<SessionStartResult> WaitForSessionStartAsync(string jobId, CancellationToken ct = default)
        {
            var startJobId = jobId;
            for (var hop = 0; hop < MaxStartSessionChainHops; hop++)
            {
                var completion = await WaitForJobAsync(startJobId, StartSessionJobTimeout, ct).ConfigureAwait(false);
                if (completion == null)
                    return SessionStartResult.Failure($"start_session job {startJobId} did not complete within timeout");
                if (!completion.IsSuccess)
                    return SessionStartResult.Failure($"start_session job {startJobId} failed: {completion.Message}");

                var sessionId = await FetchResultSessionIdAsync(startJobId, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(sessionId))
                    return SessionStartResult.Success(sessionId!);

                // session_id が無い成功 = イメージ未ビルドで BUILD_IMAGE が chain された
                var buildJobId = MatchJobId(BuildJobIdPattern, completion.Message);
                if (buildJobId == null)
                    return SessionStartResult.Failure($"start_session job {startJobId} succeeded but session_id could not be resolved");

                ResoniteMod.Msg($"start_session job {startJobId} chained image build job {buildJobId}, waiting for it");
                var build = await WaitForJobAsync(buildJobId, BuildImageJobTimeout, ct).ConfigureAwait(false);
                if (build == null)
                    return SessionStartResult.Failure($"build_image job {buildJobId} did not complete within timeout");
                if (!build.IsSuccess)
                    return SessionStartResult.Failure($"build_image job {buildJobId} failed: {build.Message}");

                var nextJobId = MatchJobId(SessionStartJobIdPattern, build.Message);
                if (nextJobId == null)
                    return SessionStartResult.Failure($"build_image job {buildJobId} succeeded but chained start_session job could not be resolved");
                startJobId = nextJobId;
            }
            return SessionStartResult.Failure($"start_session job {jobId} exceeded max chain hops");
        }

        private async Task<string?> FetchResultSessionIdAsync(string jobId, CancellationToken ct)
        {
            var res = await _client.GetAsyncJobAsync(new GetAsyncJobRequest { Id = jobId }, ct).ConfigureAwait(false);
            var payload = res.Job?.ResultPayload;
            if (string.IsNullOrEmpty(payload)) return null;
            try
            {
                using var doc = JsonDocument.Parse(payload!);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("session_id", out var sid)
                    && sid.ValueKind == JsonValueKind.String
                    ? sid.GetString()
                    : null;
            }
            catch (JsonException ex)
            {
                ResoniteMod.Warn($"NotificationSubscriber: invalid result_payload of job {jobId}: {ex.Message}");
                return null;
            }
        }

        private static string? MatchJobId(Regex pattern, string message)
        {
            var m = pattern.Match(message ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        internal class PendingJob
        {
            public TaskCompletionSource<JobCompletion> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal readonly record struct RecentCompletion(JobCompletion Completion, DateTime ReceivedAtUtc);

        /// <summary>Job 完了の結果</summary>
        internal record JobCompletion(bool IsSuccess, string Message);

        /// <summary>StartWorld で開始された session の解決結果</summary>
        internal record SessionStartResult(bool IsSuccess, string? SessionId, string? Error)
        {
            public static SessionStartResult Success(string sessionId) => new(true, sessionId, null);
            public static SessionStartResult Failure(string error) => new(false, null, error);
        }
    }
}
