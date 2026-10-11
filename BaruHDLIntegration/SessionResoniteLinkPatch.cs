using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using Hdlctrl.V1;
using ResoniteModLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace BaruHDLIntegration
{
    /// <summary>
    /// セッションダイアログ設定タブの「ResoniteLinkを有効化」ボタンを、controller でアクセスできるヘッドレスセッションでも押せるようにする。
    /// 押すと controller に ResoniteLink 接続 URL を発行させ、クリップボードにコピーしつつポート表示欄に表示する
    /// </summary>
    [HarmonyPatch(typeof(SessionControlDialog))]
    static class SessionResoniteLinkPatch
    {
        private enum Access
        {
            Checking,
            Accessible,
            Inaccessible,
        }

        private sealed class LinkState
        {
            public Access Access = Access.Checking;
            public DateTime CheckedAt;
            public bool Issuing;
            public string? Url;
            public string? DisplayUrl;
            public DateTime ExpiresAt;
            public DateTime CopiedAt;
            public string? Error;
        }

        // アクセス不可 (controller 管理外 / 通信失敗) だったセッションを再確認する間隔
        private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
        // 発行済み URL の残り有効期間がこれを切っていたら再発行する
        private static readonly TimeSpan ReissueMargin = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan CopiedNoticeDuration = TimeSpan.FromSeconds(2);
        // URL にはトークンが含まれていて長いので、表示は先頭だけにする
        private const int MaxDisplayUrlLength = 72;

        private static readonly FieldInfo _resoniteLinkPortField = AccessTools.Field(typeof(SessionControlDialog), "_resoniteLinkPort");

        // session id → 状態。読み書きは Userspace ワールドの更新スレッドからのみ行う
        private static readonly Dictionary<string, LinkState> _states = new();

        /// <summary>
        /// 自分がホストではなく、controller 経由で扱える可能性のあるセッションなら状態を返す (初回はアクセス確認を開始する)
        /// </summary>
        private static LinkState? GetState(World? world)
        {
            if (world == null || world.IsAuthority) return null;
            var sessionId = world.SessionId;
            if (string.IsNullOrEmpty(sessionId)) return null;
            if (string.IsNullOrEmpty(BaruHDLIntegration._config?.GetValue(BaruHDLIntegration.ControllerGrpcAddressKey))) return null;

            if (!_states.TryGetValue(sessionId, out var state))
            {
                state = new LinkState();
                _states[sessionId] = state;
                CheckAccess(sessionId, state);
            }
            else if (state.Access == Access.Inaccessible && DateTime.UtcNow - state.CheckedAt > RecheckInterval)
            {
                state.Access = Access.Checking;
                CheckAccess(sessionId, state);
            }
            return state;
        }

        private static void CheckAccess(string sessionId, LinkState state)
        {
            var userspace = Userspace.UserspaceWorld;
            userspace.Coroutines.StartBackgroundTask(async () =>
            {
                var accessible = false;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    accessible = (await client.GetSessionDetailsAsync(new GetSessionDetailsRequest { SessionId = sessionId })).Session != null;
                }
                catch (Exception ex)
                {
                    ResoniteMod.Debug($"Session {sessionId} is not accessible via controller: {ex.Message}");
                }
                userspace.RunSynchronously(() =>
                {
                    state.Access = accessible ? Access.Accessible : Access.Inaccessible;
                    state.CheckedAt = DateTime.UtcNow;
                });
            });
        }

        /// <summary>
        /// ポート表示欄に出す文字列。null の場合は本体の表示のままにする
        /// </summary>
        private static string? GetDisplayText(LinkState state)
        {
            if (state.Access != Access.Accessible) return null;
            if (state.Issuing) return "接続URLを発行中...";
            if (state.Error != null) return state.Error;
            var now = DateTime.UtcNow;
            if (state.Url == null || state.ExpiresAt <= now) return "ヘッドレス: ボタンを押すと接続URLを発行します";
            if (now - state.CopiedAt < CopiedNoticeDuration) return "接続URLをコピーしました";
            return state.DisplayUrl;
        }

        private static void IssueAndCopy(string sessionId, LinkState state)
        {
            if (state.Issuing) return;
            if (state.Url != null && state.ExpiresAt - DateTime.UtcNow > ReissueMargin)
            {
                CopyToClipboard(state, state.Url);
                return;
            }

            state.Issuing = true;
            state.Error = null;
            var userspace = Userspace.UserspaceWorld;
            userspace.Coroutines.StartBackgroundTask(async () =>
            {
                string? url = null;
                var expiresAt = DateTime.MaxValue;
                string? error = null;
                try
                {
                    var client = BaruHDLIntegration.GetClient();
                    var res = await client.IssueResoniteLinkConnectionAsync(new IssueResoniteLinkConnectionRequest { SessionId = sessionId });
                    url = BuildWsUrl(res.WsPath);
                    if (res.ExpiresAt.HasValue) expiresAt = res.ExpiresAt.Value.ToUniversalTime();
                }
                catch (Exception ex)
                {
                    ResoniteMod.Warn($"Failed to issue ResoniteLink connection: {ex}");
                    error = $"接続URLの発行に失敗: {ex.Message}";
                }
                userspace.RunSynchronously(() =>
                {
                    state.Issuing = false;
                    state.Error = error;
                    if (url == null) return;
                    state.Url = url;
                    state.DisplayUrl = url.Length > MaxDisplayUrlLength ? url.Substring(0, MaxDisplayUrlLength) + "…" : url;
                    state.ExpiresAt = expiresAt;
                    CopyToClipboard(state, url);
                });
            });
        }

        private static void CopyToClipboard(LinkState state, string url)
        {
            var input = Userspace.UserspaceWorld.Engine.InputInterface;
            if (!input.IsClipboardSupported) return;
            input.Clipboard!.SetText(url);
            state.CopiedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// controller が返す ws_path (path + query) に、設定された controller アドレスの host を補完する
        /// </summary>
        private static string BuildWsUrl(string wsPath)
        {
            var baseUri = new Uri(BaruHDLIntegration._config!.GetValue(BaruHDLIntegration.ControllerGrpcAddressKey)!);
            var scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
            return $"{scheme}://{baseUri.Authority}{wsPath}";
        }

        /// <summary>
        /// 本体は毎フレーム、ボタンの Enabled とポート表示欄を書き直すので、その判定と書き込みを差し替える
        /// </summary>
        [HarmonyPatch("OnCommonUpdate")]
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> OnCommonUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var isAllowed = AccessTools.Method(typeof(ResoniteLinkPermissionsExtensions), nameof(ResoniteLinkPermissionsExtensions.IsAllowedToRunResoniteLink));
            var setLocaleContent = AccessTools.PropertySetter(typeof(Text), nameof(Text.LocaleContent));
            var patchedIsAllowed = false;
            var patchedSetLocaleContent = false;

            foreach (var instruction in instructions)
            {
                if (instruction.Calls(isAllowed))
                {
                    instruction.operand = AccessTools.Method(typeof(SessionResoniteLinkPatch), nameof(IsAllowedToRunResoniteLink));
                    patchedIsAllowed = true;
                }
                else if (instruction.Calls(setLocaleContent))
                {
                    // 対象の Text がポート表示欄かを判定できるよう、ダイアログ自身も渡す
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SessionResoniteLinkPatch), nameof(SetLocaleContent));
                    patchedSetLocaleContent = true;
                }
                yield return instruction;
            }

            if (!patchedIsAllowed || !patchedSetLocaleContent)
            {
                ResoniteMod.Warn($"Failed to patch ResoniteLink controls in SessionControlDialog (isAllowed: {patchedIsAllowed}, setLocaleContent: {patchedSetLocaleContent})");
            }
        }

        private static bool IsAllowedToRunResoniteLink(World world)
        {
            return world.IsAllowedToRunResoniteLink() || GetState(world)?.Access == Access.Accessible;
        }

        private static void SetLocaleContent(Text text, LocaleString content, SessionControlDialog dialog)
        {
            var state = GetState(dialog.Engine.WorldManager.FocusedWorld);
            var overrideText = state != null ? GetDisplayText(state) : null;
            if (overrideText == null || text != (_resoniteLinkPortField.GetValue(dialog) as SyncRef<Text>)?.Target)
            {
                text.LocaleContent = content;
                return;
            }

            // 本体が付けた LocaleStringDriver が残っていると Content を直接書けないので外す
            var driver = text.Content.GetLocalizedDriver();
            if (driver != null)
            {
                driver.Target.Target = null!;
                driver.Destroy();
            }
            text.Content.Value = overrideText;
        }

        /// <summary>
        /// ヘッドレスセッションではボタンを接続 URL の発行 + コピーとして動かす (自分がホストのセッションは本体の処理のまま)
        /// </summary>
        [HarmonyPatch("OnEnableResoniteLink")]
        [HarmonyPrefix]
        public static bool OnEnableResoniteLink_Prefix(SessionControlDialog __instance)
        {
            var world = __instance.Engine.WorldManager.FocusedWorld;
            var state = GetState(world);
            if (state?.Access != Access.Accessible) return true;

            IssueAndCopy(world.SessionId, state);
            return false;
        }
    }
}
