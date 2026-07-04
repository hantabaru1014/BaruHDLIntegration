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
        /// <summary>StartWorld RPC が受理された直後に呼ばれる。job_id を受け取る。UI 一覧の即時 refresh 等に使う。</summary>
        public Action<string /*jobId*/>? OnStarted { get; set; }
        /// <summary>非同期 job が SUCCESS で完了し、作成された session の詳細取得に成功した時に呼ばれる。</summary>
        public Action<Hdlctrl.V1.Session>? OnSessionCreated { get; set; }
        /// <summary>
        /// true でダッシュボード内モーダル設定を無視し、必ず invokerWorld のワールドフローティングとして開く。
        /// WorldOrb 経由の呼び出しでオーブと同じワールドに出したい場合に true にする。
        /// </summary>
        public bool ForceWorldFloating { get; set; } = false;
    }

    /// <summary>
    /// ワールドを headless で開始するためのフォームモーダル。
    /// WorldOrbPatch (WorldOrbコンテキスト) と HdlSessionDetailModal (同じ設定で開く) の両方から呼ばれる
    /// </summary>
    internal static class HdlStartWorldForm
    {
        // ワールド指定ソースのラベル: 先頭が URL、それ以降がテンプレート (_worldTemplatePresetNames と対応)
        private static readonly List<string> _worldSourceLabels = new() { "URL", "Grid", "Platform", "Blank" };
        // controller フロントエンドと同じテンプレート preset 名 (Resonite headless が認識する識別子)
        private static readonly string[] _worldTemplatePresetNames = { "grid", "platform", "blank" };


        internal static void Open(World invokerWorld, StartWorldFormContext ctx)
        {
            var world = HdlUI.ResolveModalWorld(invokerWorld, ctx.ForceWorldFloating);
            invokerWorld.Coroutines.StartBackgroundTask(async () =>
            {
                List<HeadlessHost>? hosts = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    // ホスト一覧は全件取得して稼働中のみ抽出するため大きめのページサイズ
                    // 選択中グループがあればそれに絞る (session.group_id とも一致させるため)
                    var res = await client.ListHeadlessHostAsync(new ListHeadlessHostRequest
                    {
                        Page = new PageRequest { PageIndex = 0, PageSize = HdlUI.FetchAllPageSize },
                        GroupId = HdlSelectedGroup.SelectedGroupId,
                    });
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
            var (rootSlot, ui) = HdlUI.BuildModalPanel(world, ctx.Title, new float2(900f, 760f), ctx.ForceWorldFloating);
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
                var foundIndex = hosts.FindIndex(h => h.Id == lastSelectedHostId);
                if (foundIndex >= 0) defaultHostIndex = foundIndex;
            }

            var selectedHostIndexField = ui.HorizontalElementWithLabel("ホスト", 0.4f, () =>
            {
                // ラベルに group 名を含めることでどのグループのホストか一目で分かるようにする
                var hostLabels = hosts.Select(h =>
                {
                    var idShort = (h.Id ?? "").Substring(0, Math.Min(6, (h.Id ?? "").Length));
                    var groupLabel = HdlSelectedGroup.FormatGroupLabel(h.GroupId);
                    return $"{h.Name}({idShort}) - {groupLabel}";
                }).ToList();
                return HdlUI.BuildArrowSelector(rootSlot, ui, hostLabels, defaultHostIndex);
            });

            var nameField = ui.HorizontalElementWithLabel("Name", 0.4f, () => ui.TextField());
            nameField.TargetString = ctx.DefaultName;

            // ワールドの指定方法: URL / 組み込みテンプレート
            // 初期選択: ctx.LoadWorldUrl があれば URL、なければ Grid テンプレート
            var initialSourceIndex = string.IsNullOrEmpty(ctx.LoadWorldUrl) ? 1 : 0;
            var worldSourceField = ui.HorizontalElementWithLabel("ワールド指定", 0.4f, () =>
                HdlUI.BuildArrowSelector(rootSlot, ui, _worldSourceLabels, initialSourceIndex));

            // ワールドURLは編集可能(空から開始するケースに対応)。テンプレート選択時は無視される
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

                    // ワールドソースに応じて loadWorldUrl / loadWorldPresetName のどちらか一方をセット
                    var sourceIndex = worldSourceField.Value.Value;
                    var presetName = sourceIndex >= 1 && sourceIndex - 1 < _worldTemplatePresetNames.Length
                        ? _worldTemplatePresetNames[sourceIndex - 1]
                        : null;
                    var parameters = new Headless.Rpc.WorldStartupParameters
                    {
                        Name = nameField.TargetString,
                        Description = descField.TargetString,
                        AccessLevel = ConvertFromSession(accessLevelField.Value.Value),
                        LoadWorldUrl = presetName == null ? worldUrlField.TargetString : null,
                        LoadWorldPresetName = presetName,
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
                        // session.group_id は host.group_id と必ず一致する必要があるので host のものを継承。
                        // sidebar「全て」時に SelectedGroupId=null を渡すと controller 側では personal group 扱いになり、
                        // host.group_id が normal だとミスマッチ拒否される
                        GroupId = string.IsNullOrEmpty(host.GroupId) ? null : host.GroupId,
                    };
                    var response = await client.StartWorldAsync(req);
                    ctx.OnStarted?.Invoke(response.JobId);
                    rootSlot.RunSynchronously(() => rootSlot.Destroy());

                    // OnSessionCreated が指定されていれば、job 完了通知を待って session を解決してから呼び出す
                    if (ctx.OnSessionCreated != null)
                    {
                        var jobId = response.JobId;
                        var hostId = host.Id;
                        var onSessionCreated = ctx.OnSessionCreated;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var completion = await client.Notifications.WaitForJobAsync(
                                    jobId, hostId, TimeSpan.FromSeconds(90));
                                if (completion == null)
                                {
                                    ResoniteMod.Warn($"StartWorld job {jobId} did not complete within timeout");
                                    return;
                                }
                                if (!completion.IsSuccess)
                                {
                                    ResoniteMod.Warn($"StartWorld job {jobId} failed: {completion.Message}");
                                    return;
                                }
                                if (string.IsNullOrEmpty(completion.SessionId))
                                {
                                    ResoniteMod.Warn($"StartWorld job {jobId} succeeded but session_id could not be resolved");
                                    return;
                                }
                                var detail = await client.GetSessionDetailsAsync(
                                    new Hdlctrl.V1.GetSessionDetailsRequest { SessionId = completion.SessionId! });
                                if (detail.Session != null)
                                {
                                    onSessionCreated(detail.Session);
                                }
                            }
                            catch (Exception ex)
                            {
                                ResoniteMod.Warn($"Failed to resolve session after StartWorld job {jobId}: {ex}");
                            }
                        });
                    }
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
