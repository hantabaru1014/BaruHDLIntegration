using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        internal static void Open(World invokerWorld, Action? onChanged = null)
        {
            var world = HdlUI.ResolveModalWorld(invokerWorld);
            invokerWorld.Coroutines.StartBackgroundTask(async () =>
            {
                List<HeadlessAccount>? accounts = null;
                List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>? tags = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    // sidebar でグループが選択されているならその group で account を絞る。
                    // 「全て」の時は全 readable アカウントを取得し、モーダル内のグループセレクタで
                    // client-side にフィルタする。
                    var accTask = client.ListHeadlessAccountsAsync(new ListHeadlessAccountsRequest
                    {
                        Page = new PageRequest { PageIndex = 0, PageSize = HdlUI.FetchAllPageSize },
                        GroupId = HdlSelectedGroup.SelectedGroupId,
                    });
                    var tagTask = client.ListHeadlessHostImageTagsAsync(new ListHeadlessHostImageTagsRequest());
                    await Task.WhenAll(accTask, tagTask);
                    accounts = accTask.Result.Accounts ?? new List<HeadlessAccount>();
                    tags = tagTask.Result.Tags ?? new List<ListHeadlessHostImageTagsResponse.Types.ContainerImage>();
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
            var (rootSlot, ui) = HdlUI.BuildModalPanel(world, "ホスト開始", new float2(900f, 780f));
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

            // 作成先グループ: sidebar で「全て」を選んでいる場合は選択肢を表示。
            // 特定グループが選択されているならその値に固定し、read-only 表示。
            var sidebarGroupId = HdlSelectedGroup.SelectedGroupId;
            ValueField<int>? groupSelector = null;
            List<Group> selectableGroups = new();

            // アカウント行 (グループに応じて再構築する) の state
            Slot? accountRowSlot = null;
            List<HeadlessAccount> filteredAccounts = new();
            ValueField<int>? accountSelector = null;

            string? GetGroupIdForFilter() => sidebarGroupId
                ?? (groupSelector != null && selectableGroups.Count > 0
                    ? selectableGroups[Math.Clamp(groupSelector.Value.Value, 0, selectableGroups.Count - 1)].Id
                    : null);

            void RebuildAccountRow()
            {
                if (accountRowSlot == null || accountRowSlot.IsDestroyed) return;
                accountRowSlot.DestroyChildren();
                var rowUi = new UIBuilder(accountRowSlot);
                RadiantUI_Constants.SetupDefaultStyle(rowUi);

                var groupIdForFilter = GetGroupIdForFilter();
                filteredAccounts = groupIdForFilter == null
                    ? accounts
                    : accounts.Where(a => a.GroupId == groupIdForFilter).ToList();

                accountSelector = rowUi.HorizontalElementWithLabel("Headless Account", 0.4f, () =>
                {
                    if (filteredAccounts.Count == 0)
                    {
                        rowUi.Text("(このグループにはアカウントがありません)", bestFit: true);
                        return accountRowSlot.AttachComponent<ValueField<int>>();
                    }
                    // 同一アカウントが複数グループに登録されうるので、グループで絞れていない時はグループ名も出す
                    var accLabels = filteredAccounts.Select(a => groupIdForFilter == null
                        ? $"{a.UserName} - {HdlSelectedGroup.FormatGroupLabel(a.GroupId)}"
                        : $"{a.UserName}").ToList();
                    return HdlUI.BuildArrowSelector(rootSlot, rowUi, accLabels, 0);
                });
            }

            if (sidebarGroupId != null)
            {
                HdlUI.BuildReadOnlyField(ui, "作成先グループ", HdlSelectedGroup.FormatGroupLabel(sidebarGroupId));
            }
            else
            {
                // personal を先頭に並べる (デフォルト選択候補)
                selectableGroups = HdlSelectedGroup.Groups
                    .OrderBy(g => g.Type == GroupType.Personal ? 0 : 1)
                    .ThenBy(g => g.Name)
                    .ToList();
                if (selectableGroups.Count == 0)
                {
                    ui.Text("(グループ一覧が取得できていません。サイドバーの ↻ で再取得してください)", bestFit: true);
                }
                else
                {
                    var labels = selectableGroups.Select(g => HdlSelectedGroup.FormatGroupLabel(g.Id)).ToList();
                    groupSelector = ui.HorizontalElementWithLabel("作成先グループ", 0.4f, () =>
                        HdlUI.BuildArrowSelector(rootSlot, ui, labels, 0, onChange: _ => RebuildAccountRow()));
                }
            }

            // アカウント行 placeholder
            accountRowSlot = ui.Empty("AccountRow");
            accountRowSlot.AttachComponent<HorizontalLayout>();
            accountRowSlot.AttachComponent<LayoutElement>().MinHeight.Value = 32f;
            RebuildAccountRow();

            var tagLabels = new List<string> { "(latest)" };
            tagLabels.AddRange(tags.Select(t => $"{t.Tag} - {t.ResoniteVersion}"));
            var tagSelector = ui.HorizontalElementWithLabel("Image Tag", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, tagLabels, 0));

            var policySelector = ui.HorizontalElementWithLabel("Auto Update Policy", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, HdlUI.AutoUpdatePolicyLabels, 0));

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
                    if (filteredAccounts.Count == 0 || accountSelector == null)
                    {
                        throw new InvalidOperationException("アカウントが選択できません");
                    }
                    var client = BaruHDLIntegration.GetClient();
                    var account = filteredAccounts[Math.Clamp(accountSelector.Value.Value, 0, filteredAccounts.Count - 1)];
                    string? imageTag = tagSelector.Value.Value == 0 ? null : tags[tagSelector.Value.Value - 1].Tag;
                    var policy = HdlUI.AutoUpdatePolicies[policySelector.Value.Value];

                    // 作成先グループの決定: sidebar 選択 > モーダル内選択 > アカウントの登録先グループ
                    // ヘッドレスアカウントはグループごとに登録されるので、account の group_id と一致させる
                    string? targetGroupId = GetGroupIdForFilter()
                        ?? (string.IsNullOrEmpty(account.GroupId) ? null : account.GroupId);

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
                        // account.group_id と一致する必要がある (同一グループ制約)。
                        // controller は headless_account_id のアカウントもこのグループから引く
                        GroupId = targetGroupId,
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
