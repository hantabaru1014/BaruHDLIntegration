using HarmonyLib;
using ResoniteModLoader;

namespace BaruHDLIntegration
{
    public class BaruHDLIntegration : ResoniteMod
    {
        public override string Name => "BaruHDLIntegration";
        public override string Author => "hantabaru1014";
        public override string Version => "0.0.1";

        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ControllerGrpcAddressKey = new ModConfigurationKey<string>("ControllerGrpcAddress", "Controller base address");
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ApiIdKey = new ModConfigurationKey<string>("ApiIdKey", "ID");
        [AutoRegisterConfigKey]
        public static readonly ModConfigurationKey<string> ApiPasswordKey = new ModConfigurationKey<string>("ApiPasswordKey", "Password");

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

            Harmony harmony = new Harmony("dev.baru.resonite.BaruHDLIntegration");
            harmony.PatchAll();
        }

        internal static HDLControllerClient GetClient()
        {
            if (_client is not null) return _client;

            var address = _config!.GetValue(ControllerGrpcAddressKey) ?? "";
            var id = _config.GetValue(ApiIdKey) ?? "";
            var password = _config.GetValue(ApiPasswordKey) ?? "";
            _client = new HDLControllerClient(address, id, password);

            return _client;
        }
    }
}
