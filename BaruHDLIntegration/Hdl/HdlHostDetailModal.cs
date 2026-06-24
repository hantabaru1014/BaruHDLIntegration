using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Hdlctrl.V1;
using Headless.Rpc;
using ResoniteModLoader;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// ホスト詳細・編集・管理操作モーダル。controllerフロントエンドの HostDetailPanel 相当
    /// </summary>
    internal static class HdlHostDetailModal
    {
        private static readonly AllowedAccessEntry.Types.AccessType[] _accessTypes = new[]
        {
            AllowedAccessEntry.Types.AccessType.Http,
            AllowedAccessEntry.Types.AccessType.Websocket,
            AllowedAccessEntry.Types.AccessType.OscReceiving,
            AllowedAccessEntry.Types.AccessType.OscSending,
        };

        internal static void Open(World invokerWorld, HeadlessHost host, Action? onChanged = null)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            world.RunSynchronously(() =>
            {
                var (rootSlot, ui) = HdlUI.BuildModalPanel(world, $"ホスト: {host.Name}", new float2(1100f, 880f));
                BuildContent(rootSlot, ui, host, onChanged);
            });
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, HeadlessHost host, Action? onChanged)
        {
            // 読み取り専用情報
            HdlUI.BuildReadOnlyField(ui, "ID", host.Id);
            HdlUI.BuildReadOnlyField(ui, "Status", host.Status.ToString());
            HdlUI.BuildReadOnlyField(ui, "Account", $"{host.AccountName} ({host.AccountId})");
            HdlUI.BuildReadOnlyField(ui, "Version", HdlUI.FormatVersion(host.ResoniteVersion, host.AppVersion));
            HdlUI.BuildReadOnlyField(ui, "FPS", host.Fps.ToString("F1"));
            HdlUI.BuildReadOnlyField(ui, "Memo", string.IsNullOrEmpty(host.Memo) ? "-" : host.Memo);

            var isRunning = host.Status == HeadlessHostStatus.Running || host.Status == HeadlessHostStatus.Starting;

            ui.Text("--- 設定 ---", bestFit: true);

            var settings = host.HostSettings ?? new HeadlessHostSettings();

            var nameField = ui.HorizontalElementWithLabel("Name", 0.4f, () => ui.TextField());
            nameField.TargetString = host.Name;

            var universeIdField = ui.HorizontalElementWithLabel("Universe ID", 0.4f, () => ui.TextField());
            universeIdField.TargetString = settings.UniverseId ?? "";

            var usernameField = ui.HorizontalElementWithLabel("Username Override", 0.4f, () => ui.TextField());
            usernameField.TargetString = settings.UsernameOverride ?? "";

            var tickRateField = ui.HorizontalElementWithLabel("Tick Rate", 0.4f, () => ui.TextField());
            tickRateField.TargetString = settings.TickRate.ToString("F2");

            var maxTransfersField = ui.HorizontalElementWithLabel("Max Concurrent Asset Transfers", 0.4f, () => ui.TextField());
            maxTransfersField.TargetString = settings.MaxConcurrentAssetTransfers.ToString();

            var statusText = HdlUI.BuildStatusText(ui);

            var saveSettingsBtn = ui.Button("設定を保存");
            saveSettingsBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveSettingsBtn, "保存中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var req = new UpdateHeadlessHostSettingsRequest
                    {
                        HostId = host.Id,
                        Name = nameField.TargetString,
                        UniverseId = string.IsNullOrEmpty(universeIdField.TargetString) ? null : universeIdField.TargetString,
                        UsernameOverride = string.IsNullOrEmpty(usernameField.TargetString) ? null : usernameField.TargetString,
                        TickRate = float.TryParse(tickRateField.TargetString, out var tr) ? tr : (float?)null,
                        MaxConcurrentAssetTransfers = int.TryParse(maxTransfersField.TargetString, out var mt) ? mt : (int?)null,
                    };
                    await client.UpdateHeadlessHostSettingsAsync(req);
                    onChanged?.Invoke();
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "設定を保存しました", isError));
            };

            // 折りたたみセクション
            HdlUI.BuildLazyExpandSection(ui, "Allowed URL Hosts", contentUi =>
                BuildAllowedUrlHostsBody(rootSlot, contentUi, host, settings, statusText, isRunning));

            HdlUI.BuildLazyExpandSection(ui, "Auto Spawn Items", contentUi =>
                BuildAutoSpawnItemsBody(contentUi, host, settings, statusText, onChanged));

            HdlUI.BuildLazyExpandSection(ui, "過去インスタンス", contentUi =>
                BuildPastInstancesBody(contentUi, host, statusText));

            ui.Text("--- 操作 ---", bestFit: true);

            var shutdownBtn = ui.Button("Shutdown");
            shutdownBtn.Enabled = isRunning;
            shutdownBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(shutdownBtn, "Shutting down...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.ShutdownHeadlessHostAsync(new ShutdownHeadlessHostRequest { HostId = host.Id });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            // Restart は終了済みからも実行可能(controllerでは終了済みからの再起動を許可)
            var restartBtn = ui.Button("Restart");
            restartBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(restartBtn, "Restarting...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.RestartHeadlessHostAsync(new RestartHeadlessHostRequest { HostId = host.Id, WithWorldRestart = true });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var killBtn = ui.Button("Kill (強制停止)");
            killBtn.Enabled = isRunning;
            killBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(killBtn, "Killing...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.KillHeadlessHostAsync(new KillHeadlessHostRequest { HostId = host.Id });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var deleteBtn = ui.Button("Delete (レコードごと削除)");
            deleteBtn.Enabled = !isRunning;
            deleteBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(deleteBtn, "Deleting...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.DeleteHeadlessHostAsync(new DeleteHeadlessHostRequest { HostId = host.Id });
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var logBtn = ui.Button("ログ表示");
            logBtn.LocalPressed += (b, e) =>
            {
                HdlHostLogModal.Open(rootSlot.World, host, instanceId: host.InstanceId);
            };

            var closeBtn = ui.Button("閉じる");
            closeBtn.LocalPressed += (b, e) =>
            {
                rootSlot.RunSynchronously(() => rootSlot.Destroy());
            };
        }

        private static void BuildAllowedUrlHostsBody(Slot rootSlot, UIBuilder ui, HeadlessHost host, HeadlessHostSettings settings, Text statusText, bool isRunning)
        {
            var existing = settings.AllowedUrlHosts ?? new List<AllowedAccessEntry>();
            if (existing.Count == 0)
            {
                ui.Style.MinHeight = 28f;
                ui.Style.PreferredHeight = 28f;
                ui.Text("(現在の許可リストは空)");
            }
            foreach (var entry in existing)
            {
                var ports = entry.Ports != null && entry.Ports.Count > 0 ? string.Join(",", entry.Ports) : "-";
                var types = entry.AccessTypes != null && entry.AccessTypes.Count > 0 ? string.Join(",", entry.AccessTypes) : "-";
                ui.Style.MinHeight = 28f;
                ui.Style.PreferredHeight = 28f;
                ui.Text($"{entry.Host}  ports=[{ports}]  types=[{types}]", bestFit: true, Alignment.MiddleLeft);
            }

            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            ui.Text("追加/削除:", bestFit: true);
            var hostField = ui.HorizontalElementWithLabel("Host", 0.4f, () => ui.TextField());
            hostField.TargetString = "";
            var portField = ui.HorizontalElementWithLabel("Port", 0.4f, () => ui.TextField());
            portField.TargetString = "";
            var typeSelector = ui.HorizontalElementWithLabel("AccessType", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, _accessTypes.Select(t => t.ToString()).ToList(), 0));

            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            var allowBtn = ui.Button("AllowHostAccess (許可追加)");
            allowBtn.Enabled = isRunning;
            allowBtn.LocalPressed += async (b, e) =>
            {
                if (string.IsNullOrWhiteSpace(hostField.TargetString))
                {
                    HdlUI.SetStatus(statusText, "Host を入力してください", true);
                    return;
                }
                if (!int.TryParse(portField.TargetString, out var port))
                {
                    HdlUI.SetStatus(statusText, "Port は数値を入力してください", true);
                    return;
                }
                await HdlUI.RunWithBusyButton(allowBtn, "許可追加中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.AllowHostAccessAsync(new Hdlctrl.V1.AllowHostAccessRequest
                    {
                        HostId = host.Id,
                        Request = new Headless.Rpc.AllowHostAccessRequest
                        {
                            Host = hostField.TargetString.Trim(),
                            Port = port,
                            AccessType = _accessTypes[typeSelector.Value.Value],
                        },
                    });
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "許可を追加しました(再オープンで反映)", isError));
            };

            var denyBtn = ui.Button("DenyHostAccess (上記Host/Port/Typeを削除)");
            denyBtn.Enabled = isRunning;
            denyBtn.LocalPressed += async (b, e) =>
            {
                if (string.IsNullOrWhiteSpace(hostField.TargetString))
                {
                    HdlUI.SetStatus(statusText, "Host を入力してください", true);
                    return;
                }
                int? port = int.TryParse(portField.TargetString, out var p) ? p : (int?)null;
                await HdlUI.RunWithBusyButton(denyBtn, "削除中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.DenyHostAccessAsync(new Hdlctrl.V1.DenyHostAccessRequest
                    {
                        HostId = host.Id,
                        Request = new Headless.Rpc.DenyHostAccessRequest
                        {
                            Host = hostField.TargetString.Trim(),
                            Port = port,
                            AccessType = _accessTypes[typeSelector.Value.Value],
                        },
                    });
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "許可を削除しました(再オープンで反映)", isError));
            };
        }

        private static void BuildAutoSpawnItemsBody(UIBuilder ui, HeadlessHost host, HeadlessHostSettings settings, Text statusText, Action? onChanged)
        {
            var items = (settings.AutoSpawnItems ?? new List<string>()).ToList();
            // リストコンテナ
            ui.Style.MinHeight = -1f;
            ui.Style.PreferredHeight = -1f;
            var listSlot = ui.Empty("AutoSpawnItemsList");
            listSlot.AttachComponent<VerticalLayout>();
            listSlot.AttachComponent<ContentSizeFitter>().VerticalFit.Value = SizeFit.PreferredSize;

            void RenderItems()
            {
                listSlot.RunSynchronously(() =>
                {
                    if (listSlot.IsDestroyed) return;
                    listSlot.DestroyChildren();
                    var listUi = new UIBuilder(listSlot);
                    RadiantUI_Constants.SetupDefaultStyle(listUi);
                    if (items.Count == 0)
                    {
                        listUi.Style.MinHeight = 28f;
                        listUi.Style.PreferredHeight = 28f;
                        listUi.Text("(現在のリストは空)");
                        return;
                    }
                    for (int i = 0; i < items.Count; i++)
                    {
                        var idx = i;
                        listUi.Style.MinHeight = 32f;
                        listUi.Style.PreferredHeight = 32f;
                        listUi.HorizontalLayout(4f, 0f, 4f, 0f, 4f);
                        listUi.Style.FlexibleWidth = 100f;
                        listUi.Style.MinWidth = 0f;
                        listUi.Text(items[idx], bestFit: false, Alignment.MiddleLeft);
                        listUi.Style.FlexibleWidth = -1f;
                        listUi.Style.MinWidth = 60f;
                        var rmBtn = listUi.Button("削除");
                        rmBtn.LocalPressed += (bb, ee) =>
                        {
                            items.RemoveAt(idx);
                            RenderItems();
                        };
                        listUi.NestOut();
                    }
                });
            }
            RenderItems();

            var addField = ui.HorizontalElementWithLabel("追加 URI", 0.4f, () => ui.TextField());
            addField.TargetString = "";
            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            var addBtn = ui.Button("行追加");
            addBtn.LocalPressed += (b, e) =>
            {
                if (string.IsNullOrWhiteSpace(addField.TargetString)) return;
                items.Add(addField.TargetString.Trim());
                addField.TargetString = "";
                RenderItems();
            };

            var saveBtn = ui.Button("AutoSpawnItems を保存");
            saveBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(saveBtn, "保存中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    await client.UpdateHeadlessHostSettingsAsync(new UpdateHeadlessHostSettingsRequest
                    {
                        HostId = host.Id,
                        UpdateAutoSpawnItems = true,
                        AutoSpawnItems = items,
                    });
                    onChanged?.Invoke();
                }, (msg, isError) => HdlUI.SetStatus(statusText, isError ? msg : "AutoSpawnItems を保存しました", isError));
            };
        }

        private static void BuildPastInstancesBody(UIBuilder ui, HeadlessHost host, Text statusText)
        {
            ui.Style.MinHeight = -1f;
            ui.Style.PreferredHeight = -1f;
            var listSlot = ui.Empty("InstancesList");
            listSlot.AttachComponent<VerticalLayout>();
            listSlot.AttachComponent<ContentSizeFitter>().VerticalFit.Value = SizeFit.PreferredSize;

            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            var loadBtn = ui.Button("過去インスタンス一覧を取得");
            loadBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(loadBtn, "取得中...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var res = await client.ListHeadlessHostInstancesAsync(new ListHeadlessHostInstancesRequest { HostId = host.Id });
                    var instances = res.Instances ?? new List<ListHeadlessHostInstancesResponse.Types.Instance>();
                    listSlot.RunSynchronously(() =>
                    {
                        if (listSlot.IsDestroyed) return;
                        listSlot.DestroyChildren();
                        var listUi = new UIBuilder(listSlot);
                        RadiantUI_Constants.SetupDefaultStyle(listUi);
                        if (instances.Count == 0)
                        {
                            listUi.Style.MinHeight = 28f;
                            listUi.Style.PreferredHeight = 28f;
                            listUi.Text("(過去インスタンスなし)");
                            return;
                        }
                        listUi.Style.MinHeight = 28f;
                        listUi.Style.PreferredHeight = 28f;
                        HdlUI.BuildListHeader(listUi, new[] { "InstanceID", "FirstLog", "LastLog", "LogCount", "Current" }, new[] { 10f, 22f, 22f, 10f, 8f }, hasTrailingButton: true);
                        for (int i = 0; i < instances.Count; i++)
                        {
                            var inst = instances[i];
                            var cells = new[]
                            {
                                inst.InstanceId.ToString(),
                                inst.FirstLogAt?.ToString("MM-dd HH:mm:ss") ?? "-",
                                inst.LastLogAt?.ToString("MM-dd HH:mm:ss") ?? "-",
                                inst.LogCount.ToString(),
                                inst.IsCurrent ? "Yes" : "No",
                            };
                            listUi.Style.MinHeight = 32f;
                            listUi.Style.PreferredHeight = 32f;
                            HdlUI.BuildListRow(listUi, cells, new[] { 10f, 22f, 22f, 10f, 8f }, rowIndex: i, onClick: () =>
                            {
                                HdlHostLogModal.Open(listSlot.World, host, inst.InstanceId);
                            });
                        }
                    });
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };
        }
    }
}
