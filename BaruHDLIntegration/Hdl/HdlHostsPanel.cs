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
    /// ヘッドレスタブの「ホスト」サブタブ。controllerフロントエンドの HostList 相当
    /// </summary>
    internal static class HdlHostsPanel
    {
        private static readonly string[] _headers = { "Name", "Account", "Status", "Version", "FPS" };
        private static readonly float[] _weights = { 30f, 18f, 12f, 22f, 8f };

        private static int _refreshGeneration;
        private static int _pageIndex = 0;
        private static int _totalCount = 0;

        internal static void Build(Slot contentRoot)
        {
            var gen = ++_refreshGeneration;

            Slot listRoot = null!;
            Slot footerRoot = null!;
            void TriggerRefresh() { if (listRoot != null) Refresh(listRoot, footerRoot); }

            var ui = new UIBuilder(contentRoot);
            RadiantUI_Constants.SetupDefaultStyle(ui);
            ui.VerticalLayout(4f, 4f, forceExpandHeight: false);

            ui.Style.MinHeight = 36f;
            ui.Style.PreferredHeight = 36f;
            ui.Style.FlexibleHeight = -1f;
            ui.HorizontalLayout(8f);
            ui.Style.MinWidth = 100f;
            ui.Style.FlexibleWidth = -1f;
            var refreshBtn = ui.Button("更新");
            ui.Style.MinWidth = 120f;
            var startHostBtn = ui.Button("ホスト開始");
            startHostBtn.LocalPressed += (b, e) =>
            {
                HdlHostStartModal.Open(contentRoot.World, onChanged: TriggerRefresh);
            };
            ui.Style.FlexibleWidth = 1f;
            ui.Style.MinWidth = -1f;
            ui.Empty("Spacer");
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

            // フッター(ページネーション)スロット
            ui.Style.MinHeight = 40f;
            ui.Style.PreferredHeight = 40f;
            ui.Style.FlexibleHeight = -1f;
            footerRoot = ui.Empty("PaginationFooter");
            footerRoot.AttachComponent<HorizontalLayout>();

            refreshBtn.LocalPressed += (b, e) => TriggerRefresh();

            Refresh(listRoot, footerRoot, gen);
        }

        private static void Refresh(Slot listRoot, Slot footerRoot, int? expectedGen = null)
        {
            var gen = expectedGen ?? ++_refreshGeneration;
            listRoot.RunSynchronously(() =>
            {
                listRoot.DestroyChildren();
                var refreshUi = new UIBuilder(listRoot);
                RadiantUI_Constants.SetupDefaultStyle(refreshUi);
                refreshUi.Style.MinHeight = 32f;
                refreshUi.Style.PreferredHeight = 32f;
                refreshUi.Text("読み込み中...");
            });

            listRoot.World.Coroutines.StartBackgroundTask(async () =>
            {
                List<HeadlessHost>? hosts = null;
                PageResponse? pageInfo = null;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    var req = new ListHeadlessHostRequest
                    {
                        Page = new PageRequest { PageIndex = _pageIndex, PageSize = HdlUI.DefaultListPageSize },
                    };
                    var res = await client.ListHeadlessHostAsync(req);
                    hosts = res.Hosts ?? new List<HeadlessHost>();
                    pageInfo = res.Page;
                }
                catch (Exception ex)
                {
                    ResoniteMod.Error($"Failed to list hosts: {ex}");
                    error = ex.Message;
                }

                listRoot.RunSynchronously(() =>
                {
                    if (listRoot.IsDestroyed) return;
                    if (gen != _refreshGeneration) return;

                    // ページ情報を反映
                    if (pageInfo != null)
                    {
                        _pageIndex = pageInfo.PageIndex;
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
                    else if (hosts == null || hosts.Count == 0)
                    {
                        ui.Style.MinHeight = 32f;
                        ui.Style.PreferredHeight = 32f;
                        ui.Text("ホストがありません");
                    }
                    else
                    {
                        for (int i = 0; i < hosts.Count; i++)
                        {
                            var host = hosts[i];
                            var cells = new[]
                            {
                                HdlUI.FormatHostDisplayName(host),
                                host.AccountName,
                                host.Status.ToString(),
                                HdlUI.FormatVersion(host.ResoniteVersion, host.AppVersion),
                                host.Fps.ToString("F1"),
                            };
                            ui.Style.MinHeight = 32f;
                            ui.Style.PreferredHeight = 32f;
                            ui.Style.FlexibleHeight = -1f;
                            HdlUI.BuildListRow(ui, cells, _weights, rowIndex: i, onClick: () =>
                            {
                                HdlHostDetailModal.Open(listRoot.World, host, onChanged: () => Refresh(listRoot, footerRoot));
                            });
                        }
                    }

                    // フッターを更新
                    if (!footerRoot.IsDestroyed)
                    {
                        footerRoot.DestroyChildren();
                        var footerUi = new UIBuilder(footerRoot);
                        RadiantUI_Constants.SetupDefaultStyle(footerUi);
                        HdlUI.BuildPaginationFooter(footerUi, _pageIndex, HdlUI.DefaultListPageSize, _totalCount, newPage =>
                        {
                            _pageIndex = newPage;
                            Refresh(listRoot, footerRoot);
                        });
                    }
                });
            });
        }
    }
}
