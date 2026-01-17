using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ResoniteModLoader;
using Hdlctrl.V1;

namespace BaruHDLIntegration
{
    public class HDLControllerClient : ControllerServiceClient
    {
        private static readonly JsonSerializerOptions _sharedJsonOptions = new()
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

        private readonly UserServiceClient _userService;
        private readonly string _id;
        private readonly string _password;
        private string? _jwtToken;

        public HDLControllerClient(string baseAddress, string id, string password, HttpClientHandler? clientHandler)
            : base(CreateHttpClient(clientHandler), baseAddress, _sharedJsonOptions)
        {
            _id = id;
            _password = password;
            // UserServiceClientも同じHttpClientとJsonOptionsを共有
            _userService = new UserServiceClient(_httpClient, baseAddress, _sharedJsonOptions);
        }

        private static HttpClient CreateHttpClient(HttpClientHandler? handler)
        {
            return handler != null ? new HttpClient(handler) : new HttpClient();
        }

        /// <summary>
        /// 認証ヘッダーを追加
        /// </summary>
        protected override void ConfigureRequest(HttpRequestMessage request)
        {
            if (_jwtToken != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            }
        }

        /// <summary>
        /// 401時にトークンを更新してリトライ（最大1回）
        /// </summary>
        protected override async Task<bool> OnRequestFailedAsync(
            HttpResponseMessage response,
            int retryCount,
            CancellationToken cancellationToken)
        {
            // リトライは1回まで
            if (retryCount > 0) return false;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await UpdateToken();
                return _jwtToken != null; // トークン取得成功ならリトライ
            }
            return false;
        }

        public async Task UpdateToken()
        {
            try
            {
                var res = await _userService.GetTokenByPasswordAsync(
                    new GetTokenByPasswordRequest { Id = _id, Password = _password });
                _jwtToken = res.Token;
            }
            catch (Exception e)
            {
                ResoniteMod.Warn($"Failed to get token: {e}");
                _jwtToken = null;
            }
        }
    }
}
