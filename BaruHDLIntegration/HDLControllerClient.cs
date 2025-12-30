using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ResoniteModLoader;
using Hdlctrl.V1;

namespace BaruHDLIntegration
{
    public class HDLControllerClient
    {
        public const string USER_SERVICE = "hdlctrl.v1.UserService";
        public const string CONTROLLER_SERVICE = "hdlctrl.v1.ControllerService";

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(),
                new Rfc3339DateTimeConverter(),
                new Rfc3339NullableDateTimeConverter()
            }
        };

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

            return res.Hosts ?? new List<HeadlessHost>();
        }

        /// <summary>
        /// Worldを指定したHeadlessHostで開く
        /// </summary>
        public async Task<Hdlctrl.V1.Session?> StartWorld(Hdlctrl.V1.StartWorldRequest request)
        {
            var res = await Request<Hdlctrl.V1.StartWorldRequest, Hdlctrl.V1.StartWorldResponse>(CONTROLLER_SERVICE, "StartWorld", request);

            return res.OpenedSession;
        }

        public async Task<Hdlctrl.V1.Session?> GetSession(string sessionId)
        {
            var res = await Request<GetSessionDetailsRequest, GetSessionDetailsResponse>(CONTROLLER_SERVICE, "GetSessionDetails", new GetSessionDetailsRequest
            {
                SessionId = sessionId
            });
            return res.Session;
        }

        public async Task SaveWorld(string sessionId)
        {
            await Request<Hdlctrl.V1.SaveSessionWorldRequest, Hdlctrl.V1.SaveSessionWorldResponse>(CONTROLLER_SERVICE, "SaveSessionWorld", new Hdlctrl.V1.SaveSessionWorldRequest
            {
                SessionId = sessionId,
                SaveMode = Hdlctrl.V1.SaveSessionWorldRequest.Types.SaveMode.Overwrite,
            });
        }

        public async Task SaveWorldAs(string sessionId)
        {
            await Request<Hdlctrl.V1.SaveSessionWorldRequest, Hdlctrl.V1.SaveSessionWorldResponse>(CONTROLLER_SERVICE, "SaveSessionWorld", new Hdlctrl.V1.SaveSessionWorldRequest
            {
                SessionId = sessionId,
                SaveMode = Hdlctrl.V1.SaveSessionWorldRequest.Types.SaveMode.SaveAs,
            });
        }

        public async Task StopWorld(string sessionId)
        {
            await Request<Hdlctrl.V1.StopSessionRequest, Hdlctrl.V1.StopSessionResponse>(CONTROLLER_SERVICE, "StopSession", new Hdlctrl.V1.StopSessionRequest
            {
                SessionId = sessionId
            });
        }

        public async Task<string> UpdateUserRole(string hostId, string sessionId, string userId, string role)
        {
            var res = await Request<Hdlctrl.V1.UpdateUserRoleRequest, Hdlctrl.V1.UpdateUserRoleResponse>(CONTROLLER_SERVICE, "UpdateUserRole", new Hdlctrl.V1.UpdateUserRoleRequest
            {
                HostId = hostId,
                Parameters = new Headless.Rpc.UpdateUserRoleRequest
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
            }
            catch (Exception e)
            {
                ResoniteMod.Warn($"Failed to get token: {e}");
                _jwtToken = null;
                return;
            }
        }

        private async Task<R> Request<T, R>(string service, string rpcName, T request, bool autoUpdateToken = true)
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
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

            var responseJson = await res.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<R>(responseJson, _jsonOptions)!;
        }
    }
}
