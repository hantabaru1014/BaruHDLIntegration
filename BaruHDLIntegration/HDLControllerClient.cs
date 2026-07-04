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
            // proto3 JSON仕様により int64/uint64 は JSON 文字列で送られるので、文字列からの読み取りを許可する
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
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

        internal NotificationServiceClient NotificationService { get; }
        internal GroupServiceClient GroupService { get; }
        internal NotificationSubscriber Notifications { get; }

        public HDLControllerClient(string baseAddress, string id, string password, HttpClientHandler? clientHandler)
            : this(CreateHttpClient(clientHandler), baseAddress, id, password)
        {
        }

        private HDLControllerClient(HttpClient httpClient, string baseAddress, string id, string password)
            : base(httpClient, baseAddress, _sharedJsonOptions)
        {
            _id = id;
            _password = password;
            // 認証系は 3 クライアント共通なので delegate として base に注入する。
            // base 側は override より delegate を優先する仕様 (生成コード参照)。
            // ControllerServiceClient の分は自身の override で処理するため注入不要。
            _userService = new UserServiceClient(httpClient, baseAddress, _sharedJsonOptions);
            NotificationService = new NotificationServiceClient(
                httpClient, baseAddress, _sharedJsonOptions,
                configureRequest: AttachAuthHeader,
                onRequestFailed: HandleAuthFailureAsync);
            GroupService = new GroupServiceClient(
                httpClient, baseAddress, _sharedJsonOptions,
                configureRequest: AttachAuthHeader,
                onRequestFailed: HandleAuthFailureAsync);
            Notifications = new NotificationSubscriber(this);
        }

        private static HttpClient CreateHttpClient(HttpClientHandler? handler)
        {
            return handler != null ? new HttpClient(handler) : new HttpClient();
        }

        /// <summary>
        /// 認証ヘッダーを付与する共通実装。ControllerServiceClient の override と、
        /// Notification/Group への delegate 注入の両方から呼ばれる。
        /// </summary>
        private void AttachAuthHeader(HttpRequestMessage request)
        {
            if (_jwtToken != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            }
        }

        /// <summary>
        /// 401 時にトークンを更新して 1 回だけリトライする共通実装。
        /// </summary>
        private async Task<bool> HandleAuthFailureAsync(HttpResponseMessage response, int retryCount, CancellationToken cancellationToken)
        {
            if (retryCount > 0) return false;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await UpdateToken();
                return _jwtToken != null;
            }
            return false;
        }

        protected override void ConfigureRequest(HttpRequestMessage request) => AttachAuthHeader(request);

        protected override Task<bool> OnRequestFailedAsync(HttpResponseMessage response, int retryCount, CancellationToken cancellationToken)
            => HandleAuthFailureAsync(response, retryCount, cancellationToken);

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

        public void Dispose()
        {
            Notifications.Stop();
        }
    }
}
