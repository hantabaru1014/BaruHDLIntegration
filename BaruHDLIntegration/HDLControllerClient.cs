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

        public HDLControllerClient(string baseAddress, string id, string password, HttpClientHandler? clientHandler)
        {
            _client = clientHandler != null ? new HttpClient(clientHandler) : new HttpClient();
            _baseAddress = baseAddress;
            _id = id;
            _password = password;
        }

        /// <summary>
        /// ヘッドレスホスト一覧を取得する
        /// </summary>
        public async Task<IEnumerable<HeadlessHost>> ListHeadlessHost()
        {
            var res = await Request<ListHeadlessHostRequest, ListHeadlessHostResponse>(CONTROLLER_SERVICE, "ListHeadlessHost", new ListHeadlessHostRequest());

            return res.Hosts;
        }

        /// <summary>
        /// Worldを指定したHeadlessHostで開く
        /// </summary>
        public async Task<Hdlctrl.V1.Session> StartWorld(StartWorldRequest request)
        {
            var res = await Request<StartWorldRequest, StartWorldResponse>(CONTROLLER_SERVICE, "StartWorld", request);

            return res.OpenedSession;
        }

        public async Task<Hdlctrl.V1.Session> GetSession(string sessionId)
        {
            var res = await Request<GetSessionDetailsRequest, GetSessionDetailsResponse>(CONTROLLER_SERVICE, "GetSessionDetails", new GetSessionDetailsRequest
            {
                SessionId = sessionId
            });
            return res.Session;
        }

        public async Task SaveWorld(string hostId, string sessionId)
        {
            await Request<SaveSessionWorldRequest, SaveSessionWorldResponse>(CONTROLLER_SERVICE, "SaveSessionWorld", new SaveSessionWorldRequest
            {
                HostId = hostId,
                SessionId = sessionId
            });
        }

        public async Task StopWorld(string hostId, string sessionId)
        {
            await Request<StopSessionRequest, StopSessionResponse>(CONTROLLER_SERVICE, "StopSession", new StopSessionRequest
            {
                HostId = hostId,
                SessionId = sessionId
            });
        }

        public async Task<string> UpdateUserRole(string hostId, string sessionId, string userId, string role)
        {
            var res = await Request<UpdateUserRoleRequest, UpdateUserRoleResponse>(CONTROLLER_SERVICE, "UpdateUserRole", new UpdateUserRoleRequest
            {
                HostId = hostId,
                Parameters = new Headless.V1.UpdateUserRoleRequest
                {
                    SessionId = sessionId,
                    UserId = userId,
                    Role = role
                }
            });

            return res.Role;
        }

        public async Task UpdateToken()
        {
            try
            {
                var res = await Request<GetTokenByPasswordRequest, TokenSetResponse>(USER_SERVICE, "GetTokenByPassword", new GetTokenByPasswordRequest
                {
                    Id = _id,
                    Password = _password
                }, autoUpdateToken: false);
                _jwtToken = res.Token;
            } catch (Exception e)
            {
                ResoniteMod.Warn($"Failed to get token: {e}");
                _jwtToken = null;
                return;
            }
        }

        private async Task<R> Request<T, R>(string service, string rpcName, T request, bool autoUpdateToken = true) where T : IMessage where R : IMessage, new()
        {
            var json = JsonFormatter.Default.Format(request);
            if (service == USER_SERVICE)
            {
                ResoniteMod.Msg($"Request: {service}/{rpcName}");
            }
            else
            {
                ResoniteMod.Msg($"Request: {service}/{rpcName} body: {json}");
            }
            
            HttpRequestMessage MakeRequest()
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseAddress.TrimEnd('/')}/{service}/{rpcName}");
                if (_jwtToken is not null)
                {
                    req.Headers.Add("Authorization", $"Bearer {_jwtToken}");
                }
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                return req;
            }

            var res = await _client.SendAsync(MakeRequest());
            if (autoUpdateToken && res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // tokenの有効期限切れ対策
                await UpdateToken();
                res = await _client.SendAsync(MakeRequest());
            }
            res.EnsureSuccessStatusCode();
            return JsonParser.Default.Parse<R>(await res.Content.ReadAsStringAsync());
        }
    }
}
