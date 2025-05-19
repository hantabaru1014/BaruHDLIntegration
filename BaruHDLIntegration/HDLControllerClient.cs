using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using JWT.Builder;
using FrooxEngine;
using SkyFrost.Base;
using ResoniteModLoader;

namespace BaruHDLIntegration
{
    public class HDLControllerClient
    {
        public class JwtToken
        {
            public string Token { get; init; }
            public DateTime ExpiredAt { get; init; }

            public JwtToken(string token)
            {
                var decodedJwt = JwtBuilder.Create().DoNotVerifySignature().Decode(token);
                var jobj = JObject.Parse(decodedJwt);
                ExpiredAt = (new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)).AddSeconds(jobj["exp"]!.ToObject<int>());
                Token = token;
            }
        }

        public record HeadlessHost(string Id, string Name, string Status, string AccountId);

        public const string USER_SERVICE = "hdlctrl.v1.UserService";
        public const string CONTROLLER_SERVICE = "hdlctrl.v1.ControllerService";

        private readonly HttpClient _client;
        private readonly string _baseAddress;
        private readonly string _id;
        private readonly string _password;

        private JwtToken? _jwtToken;

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
            await UpdateToken();

            var req = CreateRequest(CONTROLLER_SERVICE, "ListHeadlessHost");
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var resObj = JObject.Parse(await res.Content.ReadAsStringAsync());
            if (resObj.ContainsKey("hosts"))
            {
                ResoniteMod.Msg(resObj.ToString());
                return ((JArray)resObj["hosts"]!).Select(h =>
                {
                    var accountId = h.Value<string>("accountId") ?? "";
                    return new HeadlessHost(h["id"]!.ToString(), h["name"]!.ToString(), h["status"]!.ToString(), accountId);
                });
            }
            throw new Exception("failed parse listHost");
        }

        /// <summary>
        /// Worldを指定したHeadlessHostで開く
        /// </summary>
        /// <param name="hostId"></param>
        /// <param name="startSettings"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task<string> StartWorld(string hostId, WorldStartSettings startSettings)
        {
            await UpdateToken();

            var worldParams = new JObject();
            worldParams["sessionName"] = startSettings.FetchedWorldName.ToString();
            //worldParams["maxUsers"] = startSettings.Link.World.MaxUsers;
            worldParams["accessLevel"] = startSettings.DefaultAccessLevel switch
            {
                SessionAccessLevel.Private => "ACCESS_LEVEL_PRIVATE",
                SessionAccessLevel.LAN => "ACCESS_LEVEL_LAN",
                SessionAccessLevel.Contacts => "ACCESS_LEVEL_CONTACTS",
                SessionAccessLevel.ContactsPlus => "ACCESS_LEVEL_CONTACTS_PLUS",
                SessionAccessLevel.RegisteredUsers => "ACCESS_LEVEL_REGISTERED_USERS",
                SessionAccessLevel.Anyone => "ACCESS_LEVEL_ANYONE",
                _ => throw new Exception("Invalid Access Level")
            };
            worldParams["loadWorldUrl"] = startSettings.Link.URL.ToString();

            var reqBody = new JObject();
            reqBody["hostId"] = hostId;
            reqBody["parameters"] = worldParams;

            ResoniteMod.Msg(reqBody.ToString());

            var req = CreateRequest(CONTROLLER_SERVICE, "StartWorld");
            req.Content = new StringContent(reqBody.ToString(), Encoding.UTF8, "application/json");
            
            var res = await _client.SendAsync(req);
            res.EnsureSuccessStatusCode();
            var resObj = JObject.Parse(await res.Content.ReadAsStringAsync());

            ResoniteMod.Msg(resObj.ToString());
            
            if (resObj.ContainsKey("openedSession") && (((JObject?)resObj["openedSession"])?.ContainsKey("id") ?? false))
            {
                return ((JObject)resObj["openedSession"]!)["id"]!.ToString();
            }
            
            throw new Exception("Failed parse started world response!");
        }

        public async Task UpdateToken()
        {
            // 有効期間内のトークンがあれば何もしない
            if (_jwtToken is not null && _jwtToken.ExpiredAt > DateTime.Now) return;

            var req = CreateRequest(USER_SERVICE, "GetTokenByPassword");
            req.Content = new StringContent($"{{\"id\": \"{_id}\", \"password\": \"{_password}\"}}", Encoding.UTF8, "application/json");
            
            var res = await _client.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                _jwtToken = null;
                return;
            }
            var resObj = JObject.Parse(await res.Content.ReadAsStringAsync());
            if (resObj.ContainsKey("token"))
            {
                _jwtToken = new JwtToken(resObj["token"]!.ToString());
            }
            else
            {
                _jwtToken = null;
            }
        }

        private HttpRequestMessage CreateRequest(string service, string rpcName)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseAddress}/{service}/{rpcName}");
            if (_jwtToken is not null)
            {
                request.Headers.Add("Authorization", $"Bearer {_jwtToken.Token}");
            }
            // リクエストのmessageが空のrpcでも空のobjectは必要
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return request;
        }
    }
}
