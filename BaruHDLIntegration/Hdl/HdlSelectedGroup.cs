using System.Collections.Generic;
using Hdlctrl.V1;

namespace BaruHDLIntegration.Hdl
{
    /// <summary>
    /// ユーザが選択中のグループ ID を保持する static holder。
    /// null = 未選択 (personal group / read 権限で自動フィルタされるデフォルト挙動)。
    /// UI (サイドバーセレクタ) が更新し、一覧/作成 RPC の呼び出し側が参照する。
    /// </summary>
    internal static class HdlSelectedGroup
    {
        private static string? _groupId;
        private static List<Group> _groups = new();
        // FormatGroupLabel/FindGroup を各行から呼ばれても O(1) で済むよう id → Group の索引を持つ
        private static Dictionary<string, Group> _groupById = new();

        /// <summary>選択中の group_id (null = 未選択)</summary>
        internal static string? SelectedGroupId => _groupId;

        /// <summary>取得済みグループ一覧 (キャッシュ)</summary>
        internal static IReadOnlyList<Group> Groups => _groups;

        internal static void SetSelectedGroupId(string? groupId)
        {
            _groupId = groupId;
        }

        internal static void SetGroups(List<Group> groups)
        {
            _groups = groups ?? new List<Group>();
            _groupById = new Dictionary<string, Group>(_groups.Count);
            foreach (var g in _groups)
            {
                if (!string.IsNullOrEmpty(g.Id)) _groupById[g.Id] = g;
            }
        }

        /// <summary>指定 group_id を "name [type]" 形式で整形する。キャッシュに無ければ id を含むフォールバックを返す。</summary>
        internal static string FormatGroupLabel(string? groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return "-";
            if (!_groupById.TryGetValue(groupId, out var g)) return $"(未取得: {groupId})";
            var typeLabel = g.Type switch
            {
                GroupType.Personal => "personal",
                GroupType.Normal => "normal",
                GroupType.System => "system",
                _ => "?",
            };
            return $"{g.Name} [{typeLabel}]";
        }

        /// <summary>指定 group_id のグループを返す (キャッシュから)</summary>
        internal static Group? FindGroup(string? groupId)
        {
            if (string.IsNullOrEmpty(groupId)) return null;
            return _groupById.TryGetValue(groupId, out var g) ? g : null;
        }
    }
}
