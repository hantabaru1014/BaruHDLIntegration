using System;
using System.Collections.Concurrent;
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
        private readonly HDLControllerClient _client;
        private CancellationTokenSource? _cts;
        private volatile bool _started;
        private readonly object _startLock = new();

        // 完了待ちの job_id → PendingJob 情報
        private readonly ConcurrentDictionary<string, PendingJob> _pendingJobs = new();

        // 直近の SessionLifecycleEvent(Started) を host_id 別に 1 件だけ保持する。
        // JobCompleted が SessionLifecycle より先に届くケースでの候補特定に使う。
        // 各 host あたり 1 エントリなので TTL や prune 不要 (次の Started で上書きされる)。
        private readonly ConcurrentDictionary<string, RecentSessionStarted> _latestStartedByHost = new();

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
            if (ev.SessionLifecycle is { } sl && sl.Kind == SessionLifecycleEvent.Types.Kind.Started)
            {
                var occurredAt = ev.OccurredAt ?? DateTime.UtcNow;
                _latestStartedByHost[sl.HostId] = new RecentSessionStarted(sl.SessionId, occurredAt);

                // pending job があるときだけスキャンする (typical: 0-1 件)
                if (!_pendingJobs.IsEmpty)
                {
                    foreach (var pending in _pendingJobs.Values)
                    {
                        if (pending.HostId == sl.HostId && pending.SessionIdCandidate == null)
                        {
                            pending.SessionIdCandidate = sl.SessionId;
                        }
                    }
                }
            }

            if (ev.JobCompleted is { } jc)
            {
                if (_pendingJobs.TryGetValue(jc.JobId, out var pending))
                {
                    if (jc.Level == JobCompletedEvent.Types.Level.Success)
                    {
                        var sessionId = pending.SessionIdCandidate ?? LookupRecentStarted(pending.HostId, pending.SubmittedAtUtc);
                        pending.Tcs.TrySetResult(new JobCompletion(true, jc.Message, sessionId));
                    }
                    else if (jc.Level == JobCompletedEvent.Types.Level.Error)
                    {
                        pending.Tcs.TrySetResult(new JobCompletion(false, jc.Message, null));
                    }
                }
            }
        }

        /// <summary>
        /// pending 登録後に到着した Started イベントの session_id を返す。
        /// キャッシュのイベントが submittedAt より前ならそれは別 job のもの (無視)。
        /// </summary>
        private string? LookupRecentStarted(string hostId, DateTime submittedAtUtc)
        {
            if (!_latestStartedByHost.TryGetValue(hostId, out var recent)) return null;
            return recent.OccurredAtUtc >= submittedAtUtc ? recent.SessionId : null;
        }

        /// <summary>
        /// 指定 job_id の完了を待つ。TCS が完了 or タイムアウトで戻る。
        /// job が SUCCESS の場合 SessionId は特定できていれば入る (StartWorld 用途)。
        /// stream が張れていない場合、購読ループを起動してから待機する。
        /// </summary>
        public async Task<JobCompletion?> WaitForJobAsync(string jobId, string hostId, TimeSpan timeout, CancellationToken ct = default)
        {
            Start();

            var pending = new PendingJob(hostId);
            _pendingJobs[jobId] = pending;
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(timeout);
                using (linkedCts.Token.Register(() => pending.Tcs.TrySetCanceled()))
                {
                    try
                    {
                        return await pending.Tcs.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
            }
            finally
            {
                _pendingJobs.TryRemove(jobId, out _);
            }
        }

        internal class PendingJob
        {
            public string HostId { get; }
            public DateTime SubmittedAtUtc { get; }
            public TaskCompletionSource<JobCompletion> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>SessionLifecycle が先に届いた場合の候補 session_id</summary>
            public string? SessionIdCandidate { get; set; }

            public PendingJob(string hostId)
            {
                HostId = hostId;
                SubmittedAtUtc = DateTime.UtcNow;
            }
        }

        internal readonly record struct RecentSessionStarted(string SessionId, DateTime OccurredAtUtc);

        /// <summary>Job 完了の結果</summary>
        internal record JobCompletion(bool IsSuccess, string Message, string? SessionId);
    }
}
