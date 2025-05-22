using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ResoniteModLoader;
using Google.Protobuf;
using Hdlctrl.V1;

namespace BaruHDLIntegration
{
    public class HDLControllerClient
    {
        public const string USER_SERVICE = "hdlctrl.v1.UserService";
        public const string CONTROLLER_SERVICE = "hdlctrl.v1.ControllerService";

        private readonly HttpClient _client;
        private readonly string _baseAddress;
        private readonly string _id;
        private readonly string _password;

        private string? _jwtToken;

        public HDLControllerClient(string baseAddress, string id, string password)
        {
            _client = new HttpClient();
            _baseAddress = baseAddress;
            _id = id;
            _password = password;
        }

        /// <summary>
        /// ヘッドレスホスト一覧を取得する
        /// </summary>
        /// <returns></returns>
        public async Task<IEnumerable<HeadlessHost>> ListHeadlessHost()
        {
            var res = await Request<ListHeadlessHostRequest, ListHeadlessHostResponse>(CONTROLLER_SERVICE, "ListHeadlessHost", new ListHeadlessHostRequest());

            return res.Hosts;
        }

        /// <summary>
        /// Worldを指定したHeadlessHostで開く
        /// </summary>
        /// <param name="hostId"></param>
        /// <param name="startSettings"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task<Hdlctrl.V1.Session> StartWorld(StartWorldRequest request)
        {
            var res = await Request<StartWorldRequest, StartWorldResponse>(CONTROLLER_SERVICE, "StartWorld", request);

            return res.OpenedSession;
        }

        public async Task UpdateToken()
        {
            try
            {
                var res = await Request<GetTokenByPasswordRequest, TokenSetResponse>(USER_SERVICE, "GetTokenByPassword", new GetTokenByPasswordRequest
                {
                    Id = _id,
                    Password = _password
                });
                _jwtToken = res.Token;
            } catch (Exception e)
            {
                ResoniteMod.Warn($"Failed to get token: {e}");
                _jwtToken = null;
                return;
            }
        }

        private async Task<R> Request<T, R>(string service, string rpcName, T request) where T : IMessage where R : IMessage, new()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseAddress}/{service}/{rpcName}");
            if (_jwtToken is not null)
            {
                req.Headers.Add("Authorization", $"Bearer {_jwtToken}");
            }
            var reqBody = JsonFormatter.Default.Format(request);
            req.Content = new StringContent(reqBody, Encoding.UTF8, "application/json");

            var res = await _client.SendAsync(req);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // tokenの有効期限切れ対策
                await UpdateToken();
                res = await _client.SendAsync(req);
            }
            res.EnsureSuccessStatusCode();
            return JsonParser.Default.Parse<R>(await res.Content.ReadAsStringAsync());
        }
    }
}
