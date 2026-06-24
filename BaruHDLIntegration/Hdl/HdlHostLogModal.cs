using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Hdlctrl.V1;
using ResoniteModLoader;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// ホストのログビューワーモーダル。GetHeadlessHostLogsAsync を呼んでテキストで表示
    /// </summary>
    internal static class HdlHostLogModal
    {
        private const int FetchLimit = 200;

        internal static void Open(World invokerWorld, HeadlessHost host, int instanceId = 0)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            world.RunSynchronously(() =>
            {
                var (rootSlot, ui) = HdlUI.BuildModalPanel(world, $"ログ: {host.Name}", new float2(1200f, 800f));
                BuildContent(rootSlot, ui, host, instanceId);
            });
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, HeadlessHost host, int instanceId)
        {
            HdlUI.BuildReadOnlyField(ui, "Host", $"{host.Name} ({host.Id})");
            HdlUI.BuildReadOnlyField(ui, "Instance ID", instanceId == 0 ? "current" : instanceId.ToString());

            var statusText = HdlUI.BuildStatusText(ui);

            ui.Style.MinHeight = 36f;
            ui.Style.PreferredHeight = 36f;
            ui.HorizontalLayout(8f);
            ui.Style.MinWidth = 120f;
            ui.Style.FlexibleWidth = -1f;
            var refreshBtn = ui.Button("最新を取得");
            var olderBtn = ui.Button("古いログを追加");
            ui.NestOut();

            // ログ表示エリア(縦に大きく取る)
            ui.Style.MinHeight = 480f;
            ui.Style.PreferredHeight = 480f;
            ui.Style.FlexibleHeight = 1f;
            ui.ScrollArea();
            ui.VerticalLayout(2f, 4f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            var logContainer = ui.Root;
            ui.NestOut();

            var loadedLogs = new List<GetHeadlessHostLogsResponse.Types.Log>();
            long? oldestId = null;

            void RenderLogs()
            {
                logContainer.RunSynchronously(() =>
                {
                    if (logContainer.IsDestroyed) return;
                    logContainer.DestroyChildren();
                    var logUi = new UIBuilder(logContainer);
                    RadiantUI_Constants.SetupDefaultStyle(logUi);
                    if (loadedLogs.Count == 0)
                    {
                        logUi.Style.MinHeight = 28f;
                        logUi.Style.PreferredHeight = 28f;
                        logUi.Text("(ログなし)");
                        return;
                    }
                    foreach (var log in loadedLogs)
                    {
                        var ts = log.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "--";
                        var line = $"[{ts}] {log.Body}";
                        // LayoutElementの高さ指定を外し、Textコンポーネントの_totalHeight(折り返し後)を使う
                        logUi.Style.MinHeight = -1f;
                        logUi.Style.PreferredHeight = -1f;
                        logUi.Style.FlexibleHeight = -1f;
                        var t = logUi.Text(line, bestFit: false, Alignment.TopLeft);
                        t.Size.Value = 18f;
                        t.HorizontalAlign.Value = Elements.Assets.TextHorizontalAlignment.Left;
                        t.VerticalAlign.Value = Elements.Assets.TextVerticalAlignment.Top;
                        t.Color.Value = log.IsError ? new colorX(1f, 0.6f, 0.6f) : RadiantUI_Constants.Neutrals.LIGHT;
                        // 自動折り返しのため HorizontalAutoSize は OFF、VerticalAutoSize は ON で高さに合わせて折り返す
                        t.HorizontalAutoSize.Value = false;
                        t.VerticalAutoSize.Value = true;
                        t.ParseRichText.Value = false;
                    }
                });
            }

            void FetchLatest()
            {
                rootSlot.World.Coroutines.StartBackgroundTask(async () =>
                {
                    GetHeadlessHostLogsResponse? res = null;
                    string? error = null;
                    try
                    {
                        var client = BaruHDLIntegration.GetClient();
                        res = await client.GetHeadlessHostLogsAsync(new GetHeadlessHostLogsRequest
                        {
                            HostId = host.Id,
                            InstanceId = instanceId,
                            Limit = FetchLimit,
                        });
                    }
                    catch (Exception ex)
                    {
                        ResoniteMod.Error($"Failed to fetch logs: {ex}");
                        error = ex.Message;
                    }
                    rootSlot.RunSynchronously(() =>
                    {
                        if (rootSlot.IsDestroyed) return;
                        if (error != null)
                        {
                            HdlUI.SetStatus(statusText, error, true);
                            return;
                        }
                        loadedLogs = res?.Logs ?? new List<GetHeadlessHostLogsResponse.Types.Log>();
                        oldestId = loadedLogs.Count > 0 ? loadedLogs.Min(l => l.Id) : (long?)null;
                        RenderLogs();
                        HdlUI.SetStatus(statusText, $"{loadedLogs.Count}件読込", false);
                    });
                });
            }

            void FetchOlder()
            {
                if (oldestId == null) { FetchLatest(); return; }
                rootSlot.World.Coroutines.StartBackgroundTask(async () =>
                {
                    GetHeadlessHostLogsResponse? res = null;
                    string? error = null;
                    try
                    {
                        var client = BaruHDLIntegration.GetClient();
                        res = await client.GetHeadlessHostLogsAsync(new GetHeadlessHostLogsRequest
                        {
                            HostId = host.Id,
                            InstanceId = instanceId,
                            Limit = FetchLimit,
                            BeforeId = oldestId,
                        });
                    }
                    catch (Exception ex)
                    {
                        ResoniteMod.Error($"Failed to fetch older logs: {ex}");
                        error = ex.Message;
                    }
                    rootSlot.RunSynchronously(() =>
                    {
                        if (rootSlot.IsDestroyed) return;
                        if (error != null)
                        {
                            HdlUI.SetStatus(statusText, error, true);
                            return;
                        }
                        var older = res?.Logs ?? new List<GetHeadlessHostLogsResponse.Types.Log>();
                        if (older.Count == 0)
                        {
                            HdlUI.SetStatus(statusText, "これ以上古いログはありません", false);
                            return;
                        }
                        loadedLogs.InsertRange(0, older);
                        oldestId = loadedLogs.Min(l => l.Id);
                        RenderLogs();
                        HdlUI.SetStatus(statusText, $"古いログ{older.Count}件追加 (合計{loadedLogs.Count}件)", false);
                    });
                });
            }

            refreshBtn.LocalPressed += (b, e) => FetchLatest();
            olderBtn.LocalPressed += (b, e) => FetchOlder();
            FetchLatest();

            // ScrollArea設定後のStyle値が残っているので明示的にリセットしてからボタン生成
            ui.Style.MinHeight = 36f;
            ui.Style.PreferredHeight = 36f;
            ui.Style.FlexibleHeight = -1f;
            ui.Style.MinWidth = -1f;
            ui.Style.FlexibleWidth = -1f;
            var closeBtn = ui.Button("閉じる");
            closeBtn.LocalPressed += (b, e) =>
            {
                rootSlot.RunSynchronously(() => rootSlot.Destroy());
            };
        }
    }
}
