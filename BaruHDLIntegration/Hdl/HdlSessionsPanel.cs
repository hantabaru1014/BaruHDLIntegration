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
    /// ヘッドレスタブの「セッション」サブタブ。controllerフロントエンドの SessionList 相当
    /// </summary>
    internal static class HdlSessionsPanel
    {
        private static readonly string[] _headers = { "Name", "Host", "Status", "Access", "Users" };
        private static readonly float[] _weights = { 30f, 20f, 12f, 16f, 12f };
        private const int DefaultPageSize = 50;

        private static readonly (string Label, SessionStatus? Value)[] _statusFilters = new (string, SessionStatus?)[]
        {
            ("All", null),
            ("Running", SessionStatus.Running),
            ("Ended", SessionStatus.Ended),
            ("Starting", SessionStatus.Starting),
            ("Crashed", SessionStatus.Crashed),
        };

        private static Slot? _contentRoot;
        private static int _refreshGeneration;
        private static int _pageIndex = 0;
        private static int _pageSize = DefaultPageSize;
        private static int _totalCount = 0;

        private static List<HeadlessHost> _hostsCache = new();

        internal static void Build(Slot contentRoot)
        {
            _contentRoot = contentRoot;
            var gen = ++_refreshGeneration;

            ValueField<int> statusSelector = null!;
            ValueField<int> hostSelector = null!;
            Slot listRoot = null!;
            Slot footerRoot = null!;
            // フィルタ変更時はページを0にリセット
            void TriggerRefreshResetPage() { _pageIndex = 0; Refresh(listRoot, footerRoot, statusSelector, hostSelector); }
            void TriggerRefresh() { Refresh(listRoot, footerRoot, statusSelector, hostSelector); }

            var ui = new UIBuilder(contentRoot);
            RadiantUI_Constants.SetupDefaultStyle(ui);
            ui.VerticalLayout(4f, 4f, forceExpandHeight: false);

            ui.Style.MinHeight = 36f;
            ui.Style.PreferredHeight = 36f;
            ui.Style.FlexibleHeight = -1f;
            ui.HorizontalLayout(8f);

            ui.Style.MinWidth = 80f;
            ui.Style.FlexibleWidth = -1f;
            ui.Text("Status:", bestFit: true, Alignment.MiddleLeft);

            ui.Style.MinWidth = -1f;
            ui.Style.FlexibleWidth = 1f;
            statusSelector = HdlUI.BuildArrowSelector(contentRoot, ui, _statusFilters.Select(f => f.Label).ToList(), defaultIndex: 1, onChange: _ => TriggerRefreshResetPage());

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 60f;
            ui.Text("Host:", bestFit: true, Alignment.MiddleLeft);

            ui.Style.MinWidth = -1f;
            ui.Style.FlexibleWidth = 1f;
            var hostLabels = BuildHostLabels(_hostsCache);
            hostSelector = HdlUI.BuildArrowSelector(contentRoot, ui, hostLabels, defaultIndex: 0, onChange: _ => TriggerRefreshResetPage());

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 100f;
            var refreshBtn = ui.Button("更新");
            ui.Style.MinWidth = 120f;
            var startSessionBtn = ui.Button("セッション開始");
            startSessionBtn.LocalPressed += (b, e) =>
            {
                var ctx = new StartWorldFormContext
                {
                    Title = "セッション開始",
                    DefaultName = "",
                    LoadWorldUrl = "",
                    OnStarted = _ => TriggerRefresh(),
                };
                HdlStartWorldForm.Open(contentRoot.World, ctx);
            };
            ui.NestOut();

            ui.Style.MinHeight = 28f;
            ui.Style.PreferredHeight = 28f;
            ui.Style.FlexibleHeight = -1f;
            ui.Style.MinWidth = -1f;
            ui.Style.FlexibleWidth = -1f;
            HdlUI.BuildListHeader(ui, _headers, _weights, hasTrailingButton: false);

            ui.Style.MinHeight = -1f;
            ui.Style.PreferredHeight = -1f;
            ui.Style.FlexibleHeight = 1f;
            ui.ScrollArea();
            ui.VerticalLayout(2f, 0f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            listRoot = ui.Root;

            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            ui.Text("読み込み中...");
            ui.NestOut();

            // フッター(ページネーション)
            ui.Style.MinHeight = 40f;
            ui.Style.PreferredHeight = 40f;
            ui.Style.FlexibleHeight = -1f;
            footerRoot = ui.Empty("PaginationFooter");
            footerRoot.AttachComponent<HorizontalLayout>();

            refreshBtn.LocalPressed += (b, e) => TriggerRefresh();

            Refresh(listRoot, footerRoot, statusSelector, hostSelector, gen);
        }

        private static List<string> BuildHostLabels(List<HeadlessHost> hosts)
        {
            var labels = new List<string> { "All" };
            foreach (var h in hosts)
            {
                var name = string.IsNullOrEmpty(h.Name) ? h.Id.Substring(0, Math.Min(8, h.Id.Length)) : h.Name;
                labels.Add(name);
            }
            return labels;
        }

        private static void Refresh(Slot listRoot, Slot footerRoot, ValueField<int> statusSelector, ValueField<int> hostSelector, int? expectedGen = null)
        {
            var gen = expectedGen ?? ++_refreshGeneration;
            var statusFilter = _statusFilters[Math.Clamp(statusSelector.Value.Value, 0, _statusFilters.Length - 1)].Value;
            var hostIndex = hostSelector.Value.Value;

            listRoot.RunSynchronously(() =>
            {
                listRoot.DestroyChildren();
                var ui = new UIBuilder(listRoot);
                RadiantUI_Constants.SetupDefaultStyle(ui);
                ui.Style.MinHeight = 32f;
                ui.Style.PreferredHeight = 32f;
                ui.Text("読み込み中...");
            });

            listRoot.World.Coroutines.StartBackgroundTask(async () =>
            {
                List<Hdlctrl.V1.Session>? sessions = null;
                PageResponse? pageInfo = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    if (_hostsCache.Count == 0)
                    {
                        // ホスト一覧は全件取得しておきたいので大きめのページサイズ
                        var hostsRes = await client.ListHeadlessHostAsync(new ListHeadlessHostRequest { Page = new PageRequest { PageIndex = 0, PageSize = 200 } });
                        _hostsCache = hostsRes.Hosts ?? new List<HeadlessHost>();
                    }

                    string? hostIdFilter = null;
                    if (hostIndex > 0 && hostIndex - 1 < _hostsCache.Count)
                    {
                        hostIdFilter = _hostsCache[hostIndex - 1].Id;
                    }

                    var req = new SearchSessionsRequest
                    {
                        Parameters = new SearchSessionsRequest.Types.SearchParameters
                        {
                            HostId = hostIdFilter,
                            Status = statusFilter,
                        },
                        Page = new PageRequest { PageIndex = _pageIndex, PageSize = _pageSize },
                    };
                    var res = await client.SearchSessionsAsync(req);
                    sessions = res.Sessions ?? new List<Hdlctrl.V1.Session>();
                    pageInfo = res.Page;
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to search sessions: {ex}");
                    error = ex.Message;
                }

                listRoot.RunSynchronously(() =>
                {
                    if (listRoot.IsDestroyed) return;
                    if (gen != _refreshGeneration) return;

                    if (pageInfo != null)
                    {
                        _pageIndex = pageInfo.PageIndex;
                        _pageSize = pageInfo.PageSize > 0 ? pageInfo.PageSize : _pageSize;
                        _totalCount = pageInfo.TotalCount;
                    }

                    listRoot.DestroyChildren();
                    var ui = new UIBuilder(listRoot);
                    RadiantUI_Constants.SetupDefaultStyle(ui);

                    if (error != null)
                    {
                        ui.Style.MinHeight = 32f;
                        ui.Style.PreferredHeight = 32f;
                        ui.Text($"エラー: {error}");
                    }
                    else if (sessions == null || sessions.Count == 0)
                    {
                        ui.Style.MinHeight = 32f;
                        ui.Style.PreferredHeight = 32f;
                        ui.Text("セッションがありません");
                    }
                    else
                    {
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            var hostName = _hostsCache.FirstOrDefault(h => h.Id == session.HostId)?.Name ?? session.HostId.Substring(0, Math.Min(8, session.HostId.Length));
                            var access = session.CurrentState?.AccessLevel.ToString() ?? session.StartupParameters?.AccessLevel.ToString() ?? "-";
                            var users = session.CurrentState != null ? $"{session.CurrentState.UsersCount}/{session.CurrentState.MaxUsers}" : "-";

                            var cells = new string[]
                            {
                                session.Name,
                                hostName,
                                session.Status.ToString(),
                                access,
                                users,
                            };
                            ui.Style.MinHeight = 32f;
                            ui.Style.PreferredHeight = 32f;
                            ui.Style.FlexibleHeight = -1f;
                            HdlUI.BuildListRow(ui, cells, _weights, rowIndex: i, onClick: () =>
                            {
                                HdlSessionDetailModal.Open(listRoot.World, session, onChanged: () => Refresh(listRoot, footerRoot, statusSelector, hostSelector));
                            });
                        }
                    }

                    // フッター更新
                    if (!footerRoot.IsDestroyed)
                    {
                        footerRoot.DestroyChildren();
                        var footerUi = new UIBuilder(footerRoot);
                        RadiantUI_Constants.SetupDefaultStyle(footerUi);
                        HdlUI.BuildPaginationFooter(footerUi, _pageIndex, _pageSize, _totalCount, newPage =>
                        {
                            _pageIndex = newPage;
                            Refresh(listRoot, footerRoot, statusSelector, hostSelector);
                        });
                    }
                });
            });
        }
    }
}
