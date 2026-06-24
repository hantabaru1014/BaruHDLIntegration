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
    /// 新規headless hostプロセス開始モーダル。controllerフロントエンドの NewHostForm 相当
    /// </summary>
    internal static class HdlHostStartModal
    {
        private static readonly HeadlessHostAutoUpdatePolicy[] _autoUpdatePolicies = new[]
        {
            HeadlessHostAutoUpdatePolicy.Never,
            HeadlessHostAutoUpdatePolicy.UsersEmpty,
        };

        internal static void Open(World invokerWorld, Action? onChanged = null)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            invokerWorld.Coroutines.StartBackgroundTask(async () =>
            {
                List<HeadlessAccount>? accounts = null;
                List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>? tags = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    // アカウントは一括取得したいので大きめのページサイズを指定
                    var accRes = await client.ListHeadlessAccountsAsync(new ListHeadlessAccountsRequest { Page = new PageRequest { PageIndex = 0, PageSize = 200 } });
                    accounts = accRes.Accounts ?? new List<HeadlessAccount>();
                    var tagRes = await client.ListHeadlessHostImageTagsAsync(new ListHeadlessHostImageTagsRequest());
                    tags = tagRes.Tags ?? new List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>();
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to fetch accounts/tags: {ex}");
                    error = ex.Message;
                }
                world.RunSynchronously(() => BuildModal(world, accounts, tags, error, onChanged));
            });
        }

        private static void BuildModal(World world, List<HeadlessAccount>? accounts, List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>? tags, string? error, Action? onChanged)
        {
            var (rootSlot, ui) = HdlUI.BuildModalPanel(world, "ホスト開始", new float2(900f, 720f));
            if (error != null)
            {
                ui.Text($"エラー: {error}");
                return;
            }
            if (accounts == null || accounts.Count == 0)
            {
                ui.Text("ヘッドレスアカウントが登録されていません。\nwebから登録してください");
                return;
            }
            BuildContent(rootSlot, ui, accounts, tags ?? new List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>(), onChanged);
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, List<HeadlessAccount> accounts, List<ListHeadlessHostImageTagsResponse.Types.ContainerImage> tags, Action? onChanged)
        {
            var nameField = ui.HorizontalElementWithLabel("Name", 0.4f, () => ui.TextField());
            nameField.TargetString = "";

            var accountSelector = ui.HorizontalElementWithLabel("Headless Account", 0.4f, () =>
            {
                var labels = accounts.Select(a => $"{a.UserName}").ToList();
                return HdlUI.BuildArrowSelector(rootSlot, ui, labels, 0);
            });

            var tagLabels = new List<string> { "(latest)" };
            tagLabels.AddRange(tags.Select(t => $"{t.Tag} - {t.ResoniteVersion}"));
            var tagSelector = ui.HorizontalElementWithLabel("Image Tag", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, tagLabels, 0));

            var policySelector = ui.HorizontalElementWithLabel("Auto Update Policy", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, _autoUpdatePolicies.Select(p => p.ToString()).ToList(), 0));

            var memoField = ui.HorizontalElementWithLabel("Memo", 0.4f, () => ui.TextField());
            memoField.TargetString = "";

            ui.Text("--- StartupConfig ---", bestFit: true);

            var universeIdField = ui.HorizontalElementWithLabel("Universe ID", 0.4f, () => ui.TextField());
            universeIdField.TargetString = "";

            var usernameField = ui.HorizontalElementWithLabel("Username Override", 0.4f, () => ui.TextField());
            usernameField.TargetString = "";

            var statusText = HdlUI.BuildStatusText(ui);

            var startBtn = ui.Button("ホスト開始");
            startBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(startBtn, "Starting...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var account = accounts[accountSelector.Value.Value];
                    string? imageTag = tagSelector.Value.Value == 0 ? null : tags[tagSelector.Value.Value - 1].Tag;
                    var policy = _autoUpdatePolicies[policySelector.Value.Value];

                    StartupConfig? startupConfig = null;
                    if (!string.IsNullOrEmpty(universeIdField.TargetString) || !string.IsNullOrEmpty(usernameField.TargetString))
                    {
                        startupConfig = new StartupConfig
                        {
                            UniverseId = string.IsNullOrEmpty(universeIdField.TargetString) ? null : universeIdField.TargetString,
                            UsernameOverride = string.IsNullOrEmpty(usernameField.TargetString) ? null : usernameField.TargetString,
                        };
                    }

                    var req = new StartHeadlessHostRequest
                    {
                        Name = nameField.TargetString,
                        HeadlessAccountId = account.UserId,
                        ImageTag = imageTag,
                        AutoUpdatePolicy = policy,
                        Memo = memoField.TargetString,
                        StartupConfig = startupConfig,
                    };
                    await client.StartHeadlessHostAsync(req);
                    onChanged?.Invoke();
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());
                }, (msg, isError) =>
                {
                    if (isError) HdlUI.SetStatus(statusText, msg, true);
                });
            };

            var cancelBtn = ui.Button("キャンセル");
            cancelBtn.LocalPressed += (b, e) =>
            {
                rootSlot.RunSynchronously(() => rootSlot.Destroy());
            };
        }
    }
}
