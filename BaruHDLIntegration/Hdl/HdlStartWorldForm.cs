using System;
using System.Collections.Generic;
using System.Linq;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using Hdlctrl.V1;
using Headless.Rpc;
using ResoniteModLoader;
using SkyFrost.Base;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// ワールド開始フォームのコンテキスト。WorldOrb / SessionDetail(同じ設定で開く) から共通利用
    /// </summary>
    internal class StartWorldFormContext
    {
        public string Title { get; set; } = "Start World by Headless";
        public string DefaultName { get; set; } = "";
        public string LoadWorldUrl { get; set; } = "";
        public Headless.Rpc.AccessLevel DefaultAccessLevel { get; set; } = Headless.Rpc.AccessLevel.Private;
        public int? DefaultMaxUsers { get; set; }
        public string? DefaultDescription { get; set; }
        public List<string>? DefaultTags { get; set; }
        // WorldOrb用: 現在のセッションのユーザID/Role (チェックボックスでON時に渡す)
        // null だとそのチェックボックス自体を出さない
        public List<string>? AvailableUserIds { get; set; }
        public List<Headless.Rpc.DefaultUserRole>? AvailableUserRoles { get; set; }
        public string Memo { get; set; } = "Started by BaruHDLIntegration";
        public Action<Hdlctrl.V1.StartWorldResponse>? OnStarted { get; set; }
    }

    /// <summary>
    /// ワールドを headless で開始するためのフォームモーダル。
    /// WorldOrbPatch (WorldOrbコンテキスト) と HdlSessionDetailModal (同じ設定で開く) の両方から呼ばれる
    /// </summary>
    internal static class HdlStartWorldForm
    {
        internal static void Open(World invokerWorld, StartWorldFormContext ctx)
        {
            var world = invokerWorld.Engine.WorldManager.FocusedWorld ?? invokerWorld;
            invokerWorld.Coroutines.StartBackgroundTask(async () =>
            {
                List<HeadlessHost>? hosts = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    // ホスト一覧は全件取得して稼働中のみ抽出するため大きめのページサイズ
                    var res = await client.ListHeadlessHostAsync(new ListHeadlessHostRequest { Page = new PageRequest { PageIndex = 0, PageSize = 200 } });
                    hosts = (res.Hosts ?? new List<HeadlessHost>())
                        .Where(h => h.Status == HeadlessHostStatus.Running)
                        .ToList();
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to list hosts: {ex}");
                    error = ex.Message;
                }
                world.RunSynchronously(() => BuildModal(world, ctx, hosts, error));
            });
        }

        private static void BuildModal(World world, StartWorldFormContext ctx, List<HeadlessHost>? hosts, string? error)
        {
            var (rootSlot, ui) = HdlUI.BuildModalPanel(world, ctx.Title, new float2(900f, 760f));
            if (error != null)
            {
                ui.Text($"エラー: {error}");
                return;
            }
            if (hosts == null || hosts.Count == 0)
            {
                ui.Text("実行中のホストがありません！\nwebからホストを開始してください");
                return;
            }
            BuildContent(rootSlot, ui, ctx, hosts);
        }

        private static void BuildContent(Slot rootSlot, UIBuilder ui, StartWorldFormContext ctx, List<HeadlessHost> hosts)
        {
            var lastSelectedHostId = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastSelectedHostIdKey);
            var defaultHostIndex = 0;
            if (!string.IsNullOrEmpty(lastSelectedHostId))
            {
                defaultHostIndex = hosts.FindIndex(h => h.Id == lastSelectedHostId) switch
                {
                    -1 => 0,
                    var index => index
                };
            }

            var selectedHostIndexField = ui.HorizontalElementWithLabel("ホスト", 0.4f, () =>
            {
                var hostLabels = hosts.Select(h => $"{h.Name}({h.Id.Substring(0, Math.Min(6, h.Id.Length))})").ToList();
                return HdlUI.BuildArrowSelector(rootSlot, ui, hostLabels, defaultHostIndex);
            });

            var nameField = ui.HorizontalElementWithLabel("Name", 0.4f, () => ui.TextField());
            nameField.TargetString = ctx.DefaultName;

            // ワールドURLは編集可能(空から開始するケースに対応)
            var worldUrlField = ui.HorizontalElementWithLabel("World URL", 0.4f, () => ui.TextField());
            worldUrlField.TargetString = ctx.LoadWorldUrl;

            var accessLevelField = rootSlot.AttachComponent<ValueField<SessionAccessLevel>>();
            accessLevelField.Value.Value = ConvertToSession(ctx.DefaultAccessLevel);
            ui.Text("Access Level", bestFit: true);
            SessionControlDialog.GenerateAccessLevelUI(ui, accessLevelField.Value);

            var maxUsersField = ui.HorizontalElementWithLabel("Max Users", 0.4f, () => ui.TextField());
            maxUsersField.TargetString = ctx.DefaultMaxUsers?.ToString() ?? "";

            var descField = ui.HorizontalElementWithLabel("Description", 0.4f, () => ui.TextField());
            descField.TargetString = ctx.DefaultDescription ?? "";

            var tagsField = ui.HorizontalElementWithLabel("Tags (カンマ区切り)", 0.4f, () => ui.TextField());
            tagsField.TargetString = string.Join(",", ctx.DefaultTags ?? new List<string>());

            Checkbox? allowUsersField = null;
            if (ctx.AvailableUserIds != null)
            {
                allowUsersField = ui.HorizontalElementWithLabel("現在のセッションのユーザに参加許可", 0.4f, () => ui.Checkbox(true));
                allowUsersField.IsChecked = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastCheckedAllowUsersKey) ?? false;
            }

            Checkbox? keepRolesField = null;
            if (ctx.AvailableUserRoles != null)
            {
                keepRolesField = ui.HorizontalElementWithLabel("現在のセッションのユーザ権限を維持する", 0.4f, () => ui.Checkbox(true));
                keepRolesField.IsChecked = BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.LastCheckedKeepRolesKey) ?? false;
            }

            var statusText = HdlUI.BuildStatusText(ui);

            var startBtn = ui.Button("セッション開始");
            startBtn.LocalPressed += async (b, e) =>
            {
                await HdlUI.RunWithBusyButton(startBtn, "Starting...", async () =>
                {
                    var client = BaruHDLIntegration.GetClient();
                    var host = hosts[selectedHostIndexField.Value.Value];

                    if (BaruHDLIntegration._config != null)
                    {
                        BaruHDLIntegration._config.Set(BaruHDLIntegration.LastSelectedHostIdKey, host.Id);
                        if (allowUsersField != null)
                            BaruHDLIntegration._config.Set(BaruHDLIntegration.LastCheckedAllowUsersKey, allowUsersField.IsChecked);
                        if (keepRolesField != null)
                            BaruHDLIntegration._config.Set(BaruHDLIntegration.LastCheckedKeepRolesKey, keepRolesField.IsChecked);
                    }

                    var allowedIds = (allowUsersField?.IsChecked == true && ctx.AvailableUserIds != null)
                        ? ctx.AvailableUserIds
                        : new List<string>();
                    var roles = (keepRolesField?.IsChecked == true && ctx.AvailableUserRoles != null)
                        ? ctx.AvailableUserRoles
                        : new List<Headless.Rpc.DefaultUserRole>();

                    var parameters = new Headless.Rpc.WorldStartupParameters
                    {
                        Name = nameField.TargetString,
                        Description = descField.TargetString,
                        AccessLevel = ConvertFromSession(accessLevelField.Value.Value),
                        LoadWorldUrl = worldUrlField.TargetString,
                        MaxUsers = int.TryParse(maxUsersField.TargetString, out var mu) ? mu : (int?)null,
                        Tags = tagsField.TargetString.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList(),
                        JoinAllowedUserIds = allowedIds,
                        DefaultUserRoles = roles,
                    };
                    var req = new Hdlctrl.V1.StartWorldRequest
                    {
                        HostId = host.Id,
                        Parameters = parameters,
                        Memo = ctx.Memo,
                    };
                    var response = await client.StartWorldAsync(req);
                    ctx.OnStarted?.Invoke(response);
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

        internal static SessionAccessLevel ConvertToSession(Headless.Rpc.AccessLevel rpcLevel) => rpcLevel switch
        {
            Headless.Rpc.AccessLevel.Private => SessionAccessLevel.Private,
            Headless.Rpc.AccessLevel.Lan => SessionAccessLevel.LAN,
            Headless.Rpc.AccessLevel.Contacts => SessionAccessLevel.Contacts,
            Headless.Rpc.AccessLevel.ContactsPlus => SessionAccessLevel.ContactsPlus,
            Headless.Rpc.AccessLevel.RegisteredUsers => SessionAccessLevel.RegisteredUsers,
            Headless.Rpc.AccessLevel.Anyone => SessionAccessLevel.Anyone,
            _ => SessionAccessLevel.Private,
        };

        internal static Headless.Rpc.AccessLevel ConvertFromSession(SessionAccessLevel uiLevel) => uiLevel switch
        {
            SessionAccessLevel.Private => Headless.Rpc.AccessLevel.Private,
            SessionAccessLevel.LAN => Headless.Rpc.AccessLevel.Lan,
            SessionAccessLevel.Contacts => Headless.Rpc.AccessLevel.Contacts,
            SessionAccessLevel.ContactsPlus => Headless.Rpc.AccessLevel.ContactsPlus,
            SessionAccessLevel.RegisteredUsers => Headless.Rpc.AccessLevel.RegisteredUsers,
            SessionAccessLevel.Anyone => Headless.Rpc.AccessLevel.Anyone,
            _ => Headless.Rpc.AccessLevel.Private,
        };
    }
}
