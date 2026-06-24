using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteModLoader;

namespace BaruHDLIntegration.Hdl
{
    internal static class HdlUI
    {
        internal static Button BuildSubTabButton(UIBuilder ui, string label, Action onClick)
        {
            var btn = ui.Button(label);
            btn.LocalPressed += (b, e) => onClick();
            return btn;
        }

        internal static void SetSubTabButtonActive(Button button, bool isActive)
        {
            if (button == null || button.IsDestroyed) return;
            button.SetColors(isActive
                ? RadiantUI_Constants.TAB_ACTIVE_BACKGROUND_COLOR
                : RadiantUI_Constants.TAB_INACTIVE_BACKGROUND_COLOR);
        }

        private static readonly colorX RowBgEven = new colorX(1f, 1f, 1f, 0.04f);
        private static readonly colorX RowBgOdd = new colorX(1f, 1f, 1f, 0.10f);
        private static readonly colorX HeaderBg = new colorX(0.2f, 0.2f, 0.3f, 0.5f);

        private const float CellTextSize = 24f;
        private const float CellLeftPadding = 8f;
        private const float CellRightPadding = 8f;
        private const float TrailingButtonWeight = 4f;
        // 列の重みに対する視覚幅(半角=1, 全角=2)の上限係数。
        // 経験則: 行の総幅 ÷ 列重み合計 ÷ 1文字あたりピクセル を元に少し余裕を持たせる
        private const float VisualUnitsPerWeight = 0.8f;
        private const int MinVisualWidth = 4;
        // "…" を全角1文字相当として扱うため余白を取る
        private const int EllipsisVisualWidth = 2;

        // richテキストタグ ( <color=...>, </color>, <b>, <i>, <size=...> 等 ) の除去パターン
        private static readonly Regex RichTextTagPattern = new Regex(@"<[^<>]+>", RegexOptions.Compiled);

        private static int CharVisualWidth(char c) => c < 0x80 ? 1 : 2;

        private static string TruncateByVisualWidth(string s, int maxVisualWidth)
        {
            var sb = new StringBuilder();
            int width = 0;
            foreach (var c in s)
            {
                int cw = CharVisualWidth(c);
                if (width + cw > maxVisualWidth - EllipsisVisualWidth)
                {
                    sb.Append('…');
                    return sb.ToString();
                }
                sb.Append(c);
                width += cw;
            }
            return s;
        }

        private static string SanitizeForCell(string? content, float weight)
        {
            if (string.IsNullOrEmpty(content)) return "";
            // 制御文字・連続空白(改行含む)を単一スペースに集約
            var sb = new StringBuilder(content!.Length);
            foreach (var c in content)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                }
            }
            var line = sb.ToString().Trim();
            // richテキストタグを剥がす(ParseRichText=falseにしているが念のため)
            line = RichTextTagPattern.Replace(line, "");
            // 視覚幅(全角=2, 半角=1)で省略
            var maxVisualWidth = Math.Max(MinVisualWidth, (int)(weight * VisualUnitsPerWeight));
            return TruncateByVisualWidth(line, maxVisualWidth);
        }

        private static Text PlaceCellText(Slot cellSlot, string content, float weight)
        {
            // Cellslotにテキストを直接アタッチ。SplitHorizontally で得た RectTransform が
            // 列の絶対位置を決めているので、テキストは その rect の中で左寄せ・上下中央に置く
            var textSlot = cellSlot.AddSlot("Text");
            var rt = textSlot.AttachComponent<RectTransform>();
            // 左右に少しパディング(オフセット)を入れる
            rt.OffsetMin.Value = new float2(CellLeftPadding, 0f);
            rt.OffsetMax.Value = new float2(-CellRightPadding, 0f);

            var t = textSlot.AttachComponent<Text>();
            t.Content.Value = SanitizeForCell(content, weight);
            t.Size.Value = CellTextSize;
            t.Color.Value = RadiantUI_Constants.TEXT_COLOR;
            t.HorizontalAlign.Value = TextHorizontalAlignment.Left;
            t.VerticalAlign.Value = TextVerticalAlignment.Middle;
            t.HorizontalAutoSize.Value = false;
            t.VerticalAutoSize.Value = false;
            t.ParseRichText.Value = false;
            return t;
        }

        internal static void BuildListRow(UIBuilder ui, IList<string> cells, IList<float> weights, int rowIndex = 0, Action? onClick = null)
        {
            ui.Panel(rowIndex % 2 == 0 ? RowBgEven : RowBgOdd);
            var splitWeights = onClick != null
                ? weights.Concat(new[] { TrailingButtonWeight }).ToArray()
                : weights.ToArray();
            var rects = ui.SplitHorizontally(splitWeights);

            for (int i = 0; i < cells.Count; i++)
            {
                PlaceCellText(rects[i].Slot, cells[i], weights[i]);
            }

            if (onClick != null)
            {
                var btnSlot = rects[cells.Count].Slot;
                var btnUi = new UIBuilder(btnSlot);
                RadiantUI_Constants.SetupDefaultStyle(btnUi);
                var btn = btnUi.Button(">");
                btn.LocalPressed += (b, e) => onClick();
            }

            ui.NestOut(); // exit Panel
        }

        internal static void BuildListHeader(UIBuilder ui, IList<string> headers, IList<float> weights, bool hasTrailingButton)
        {
            ui.Panel(HeaderBg);
            var splitWeights = hasTrailingButton
                ? weights.Concat(new[] { TrailingButtonWeight }).ToArray()
                : weights.ToArray();
            var rects = ui.SplitHorizontally(splitWeights);

            for (int i = 0; i < headers.Count; i++)
            {
                var t = PlaceCellText(rects[i].Slot, headers[i], weights[i]);
                t.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;
            }

            ui.NestOut(); // exit Panel
        }

        /// <summary>
        /// ユーザーの前にフローティングパネル(モーダル)を出す。閉じる際は rootSlot.Destroy() を呼ぶこと
        /// </summary>
        internal static (Slot rootSlot, UIBuilder ui) BuildModalPanel(World world, string title, float2 size)
        {
            var rootSlot = world.AddSlot(title, persistent: false);
            rootSlot.PositionInFrontOfUser(float3.Backward);
            var ui = RadiantUI_Panel.SetupPanel(rootSlot, title, size);
            rootSlot.LocalScale *= 0.0005f;
            RadiantUI_Constants.SetupEditorStyle(ui);
            rootSlot.SetContainerTitle(title);
            ui.ScrollArea();
            ui.VerticalLayout(4f, 0f);
            ui.FitContent(SizeFit.Disabled, SizeFit.PreferredSize);
            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            return (rootSlot, ui);
        }

        /// <summary>
        /// 読み取り専用の "label : value" 行をモーダル内に追加する
        /// </summary>
        internal static void BuildReadOnlyField(UIBuilder ui, string label, string value)
        {
            ui.HorizontalElementWithLabel(label, 0.4f, () =>
            {
                var t = ui.Text(value, bestFit: true, Alignment.MiddleLeft);
                t.Color.Value = RadiantUI_Constants.Neutrals.LIGHT;
                return t;
            });
        }

        /// <summary>
        /// 一覧下部のページネーションフッターを構築する。
        /// pageIndex は 0 始まり。onPageChanged で新しい pageIndex を渡される
        /// </summary>
        internal static void BuildPaginationFooter(UIBuilder ui, int pageIndex, int pageSize, int totalCount, Action<int> onPageChanged)
        {
            var totalPages = pageSize > 0 ? Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)) : 1;
            var currentPage = pageIndex + 1;

            ui.HorizontalLayout(8f, 0f, 8f, 0f, 8f);

            ui.Style.MinWidth = 60f;
            ui.Style.FlexibleWidth = -1f;
            var firstBtn = ui.Button("<<");
            firstBtn.Enabled = pageIndex > 0;
            firstBtn.LocalPressed += (b, e) => onPageChanged(0);

            var prevBtn = ui.Button("<");
            prevBtn.Enabled = pageIndex > 0;
            prevBtn.LocalPressed += (b, e) => onPageChanged(Math.Max(0, pageIndex - 1));

            ui.Style.MinWidth = 0f;
            ui.Style.FlexibleWidth = 100f;
            ui.Text($"Page {currentPage} / {totalPages}  ({totalCount}件)", bestFit: true, Alignment.MiddleCenter);

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 60f;
            var nextBtn = ui.Button(">");
            nextBtn.Enabled = pageIndex < totalPages - 1;
            nextBtn.LocalPressed += (b, e) => onPageChanged(pageIndex + 1);

            var lastBtn = ui.Button(">>");
            lastBtn.Enabled = pageIndex < totalPages - 1;
            lastBtn.LocalPressed += (b, e) => onPageChanged(totalPages - 1);

            ui.NestOut();
        }

        /// <summary>
        /// 折りたたみ可能なセクション。クリックで開閉、開いた時 contentBuilder が呼ばれて中身が構築される。
        /// 閉じた時は子要素が破棄される。
        /// </summary>
        internal static void BuildLazyExpandSection(UIBuilder ui, string title, Action<UIBuilder> contentBuilder)
        {
            var prevMinH = ui.Style.MinHeight;
            var prevPrefH = ui.Style.PreferredHeight;

            ui.Style.MinHeight = 32f;
            ui.Style.PreferredHeight = 32f;
            var expandBtn = ui.Button($"▸ {title}");

            // 中身用の入れ子スロット (兄弟として置く)
            ui.Style.MinHeight = -1f;
            ui.Style.PreferredHeight = -1f;
            var contentSlot = ui.Empty("LazySection");
            contentSlot.AttachComponent<VerticalLayout>();
            contentSlot.AttachComponent<ContentSizeFitter>().VerticalFit.Value = SizeFit.PreferredSize;

            ui.Style.MinHeight = prevMinH;
            ui.Style.PreferredHeight = prevPrefH;

            bool expanded = false;
            expandBtn.LocalPressed += (b, e) =>
            {
                if (contentSlot.IsDestroyed) return;
                expanded = !expanded;
                if (expanded)
                {
                    expandBtn.LabelText = $"▾ {title}";
                    var contentUi = new UIBuilder(contentSlot);
                    RadiantUI_Constants.SetupDefaultStyle(contentUi);
                    contentBuilder(contentUi);
                }
                else
                {
                    expandBtn.LabelText = $"▸ {title}";
                    contentSlot.DestroyChildren();
                }
            };
        }

        /// <summary>
        /// 矢印選択UIを構築(WorldOrbPatchから移設)。フィールドは渡されたSlotにアタッチされる
        /// onChange は選択変更時に呼ばれる(新しいindexを渡す)
        /// </summary>
        internal static ValueField<int> BuildArrowSelector(Slot slot, UIBuilder ui, IList<string> labels, int defaultIndex = 0, Action<int>? onChange = null)
        {
            var field = slot.AttachComponent<ValueField<int>>();
            ui.HorizontalLayout(4f);

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 24f;
            var prevBtn = ui.Button("<<");

            ui.Style.FlexibleWidth = 100f;
            ui.Style.MinWidth = -1f;
            var centerBtn = ui.Button();
            centerBtn.LabelText = labels.Count == 0 ? "" : labels[Math.Min(defaultIndex, labels.Count - 1)];
            if (labels.Count > 0)
            {
                field.Value.Value = Math.Min(defaultIndex, labels.Count - 1);
            }

            ui.Style.FlexibleWidth = -1f;
            ui.Style.MinWidth = 24f;
            var nextBtn = ui.Button(">>");

            prevBtn.LocalPressed += (b, e) =>
            {
                if (labels.Count == 0) return;
                field.Value.Value = (field.Value.Value - 1 + labels.Count) % labels.Count;
                centerBtn.LabelText = labels[field.Value.Value];
                onChange?.Invoke(field.Value.Value);
            };
            nextBtn.LocalPressed += (b, e) =>
            {
                if (labels.Count == 0) return;
                field.Value.Value = (field.Value.Value + 1) % labels.Count;
                centerBtn.LabelText = labels[field.Value.Value];
                onChange?.Invoke(field.Value.Value);
            };

            ui.NestOut();

            return field;
        }

        /// <summary>
        /// 非同期処理中にボタンを無効化＆ラベル差し替え。完了/例外時に復帰
        /// onComplete(message, isError) は処理結果通知用(モーダル内ステータステキスト更新等)
        /// </summary>
        internal static async Task RunWithBusyButton(Button button, string busyLabel, Func<Task> work, Action<string, bool>? onComplete = null)
        {
            string? originalLabel = null;
            button.RunSynchronously(() =>
            {
                originalLabel = button.LabelText;
                button.Enabled = false;
                button.LabelText = busyLabel;
            });
            string? errorMsg = null;
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                ResoniteMod.Error($"Action failed: {ex}");
                errorMsg = ex.Message;
            }
            finally
            {
                button.RunSynchronously(() =>
                {
                    if (button.IsDestroyed) return;
                    button.Enabled = true;
                    if (originalLabel != null) button.LabelText = originalLabel;
                });
            }
            if (onComplete != null)
            {
                onComplete(errorMsg ?? "完了しました", errorMsg != null);
            }
        }

        /// <summary>
        /// モーダル内のステータステキストを作る。SetStatus で更新可能
        /// </summary>
        internal static Text BuildStatusText(UIBuilder ui)
        {
            var t = ui.Text("");
            t.HorizontalAlign.Value = TextHorizontalAlignment.Left;
            t.VerticalAlign.Value = TextVerticalAlignment.Middle;
            return t;
        }

        internal static void SetStatus(Text? statusText, string msg, bool isError = false)
        {
            if (statusText == null) return;
            statusText.RunSynchronously(() =>
            {
                if (statusText.IsDestroyed) return;
                statusText.Content.Value = msg;
                statusText.Color.Value = isError
                    ? new colorX(1f, 0.4f, 0.4f)
                    : new colorX(0.4f, 1f, 0.4f);
            });
        }

        /// <summary>
        /// ユーザーの現在のフォーカスワールドにワールドオーブをスポーンする
        /// </summary>
        internal static void SpawnWorldOrb(Engine engine, string? worldUrl, string worldName)
        {
            if (string.IsNullOrEmpty(worldUrl)) return;
            var world = engine.WorldManager.FocusedWorld;
            if (world == null) return;
            world.RunSynchronously(() =>
            {
                var slot = world.RootSlot.LocalUserSpace.AddSlot("World Orb");
                var orb = slot.AttachComponent<WorldOrb>();
                orb.URL = new Uri(worldUrl!);
                orb.WorldName = worldName;
                slot.PositionInFrontOfUser();
            });
        }

        /// <summary>
        /// ユーザーの現在のフォーカスワールドにセッションオーブをスポーンする
        /// connectUris が空のときは ressession:///{sessionId} にフォールバック
        /// </summary>
        internal static void SpawnSessionOrb(Engine engine, string sessionId, IList<string>? connectUris, string sessionName, int userCount)
        {
            var world = engine.WorldManager.FocusedWorld;
            if (world == null) return;
            world.RunSynchronously(() =>
            {
                var slot = world.RootSlot.LocalUserSpace.AddSlot("Session Orb");
                var orb = slot.AttachComponent<WorldOrb>();
                var uris = (connectUris != null && connectUris.Count > 0)
                    ? connectUris.Select(s => new Uri(s)).ToList()
                    : new List<Uri> { new Uri($"ressession:///{sessionId}") };
                orb.ActiveSessionURLs = uris;
                orb.ActiveUsers.Value = userCount;
                orb.WorldName = sessionName;
                slot.PositionInFrontOfUser();
            });
        }
    }
}
