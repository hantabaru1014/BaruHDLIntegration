using HarmonyLib;
using ResoniteModLoader;
using System.Net;
using System.Net.Http;

namespace BaruHDLIntegration
{
    public class BaruHDLIntegration : ResoniteMod
    {
        public override string Name => "BaruHDLIntegration";
        public override string Author => "hantabaru1014";
        public override string Version => "0.0.3";

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ControllerGrpcAddressKey = new ModConfigurationKey<string>("ControllerGrpcAddress", "Controller base address");
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ApiIdKey = new ModConfigurationKey<string>("ApiIdKey", "ID");
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ApiPasswordKey = new ModConfigurationKey<string>("ApiPasswordKey", "Password");
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> EnabledProxyKey = new ModConfigurationKey<bool>("EnabledProxy", "Enabled proxy", () => false);
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ProxyAddressKey = new ModConfigurationKey<string>("ProxyAddress", "Proxy URL");

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> LastSelectedHostIdKey = new ModConfigurationKey<string>("_LastSelectedHostId", computeDefault: () => string.Empty, internalAccessOnly: true);
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> LastCheckedAllowUsersKey = new ModConfigurationKey<bool>("_LastCheckedAllowUsers", computeDefault: () => false, internalAccessOnly: true);
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<bool> LastCheckedKeepRolesKey = new ModConfigurationKey<bool>("_LastCheckedKeepRoles", computeDefault: () => false, internalAccessOnly: true);

        internal static ModConfiguration? _config;

        private static HDLControllerClient? _client;

        public override void OnEngineInit()
        {
            _config = GetConfiguration();
            if (_config == null)
            {
                Error("Config Not Found!!");
                return;
            }
            _config.OnThisConfigurationChanged += _config_OnThisConfigurationChanged;

            new Harmony("dev.baru.resonite.BaruHDLIntegration").PatchAll();
        }

        private void _config_OnThisConfigurationChanged(ConfigurationChangedEvent configurationChangedEvent)
        {
            if (_client is not null)
            {
                _client = MakeClient();
            }
        }

        internal static HDLControllerClient GetClient()
        {
            if (_client is not null) return _client;
            _client = MakeClient();

            return _client;
        }

        private static HDLControllerClient MakeClient()
        {
            var address = _config!.GetValue(ControllerGrpcAddressKey) ?? "";
            var id = _config.GetValue(ApiIdKey) ?? "";
            var password = _config.GetValue(ApiPasswordKey) ?? "";
            if (_config.GetValue(EnabledProxyKey) && !string.IsNullOrEmpty(_config.GetValue(ProxyAddressKey)))
            {
                var handler = new HttpClientHandler
                {
                    Proxy = new WebProxy(_config.GetValue(ProxyAddressKey)),
                    UseProxy = true
                };
                return new HDLControllerClient(address, id, password, handler);
            }
            return new HDLControllerClient(address, id, password, null);
        }
    }
}
