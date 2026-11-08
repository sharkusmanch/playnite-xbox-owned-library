using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XboxOwnedLibrary.Services
{
    /// <summary>
    /// Signs in with xbox.com's own client through the OAuth device-code flow.
    /// The Store's collections service answers per calling app: other clients
    /// (including the one the built-in Xbox plugin uses) can get an empty list,
    /// while xbox.com's client gets the account's real library.
    /// </summary>
    public class XboxAuth
    {
        public const string ClientId = "1f907974-e22b-4810-a9de-d9647380c97e";
        private const string Scope = "XboxLive.signin offline_access";
        private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
        private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
        private const string UserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";

        public const string XboxLiveRelyingParty = "http://xboxlive.com";
        public const string MarketplaceRelyingParty = "http://mp.microsoft.com/";

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly HttpClient _http;
        private readonly string _refreshTokenPath;

        public XboxAuth(HttpClient http, string dataDir)
        {
            _http = http;
            _refreshTokenPath = Path.Combine(dataDir, "refresh_token.bin");
        }

        public bool HasSavedSignIn => File.Exists(_refreshTokenPath);

        public void SignOut()
        {
            if (File.Exists(_refreshTokenPath))
            {
                File.Delete(_refreshTokenPath);
            }
        }

        public class DeviceCode
        {
            public string device_code { get; set; }
            public string user_code { get; set; }
            public string verification_uri { get; set; }
            public int expires_in { get; set; }
            public int interval { get; set; }

            [DontSerialize]
            public string LinkWithCode => $"{(string.IsNullOrEmpty(verification_uri) ? "https://www.microsoft.com/link" : verification_uri)}?otc={Uri.EscapeDataString(user_code)}";
        }

        private class TokenResponse
        {
            public string access_token { get; set; }
            public string refresh_token { get; set; }
            public string error { get; set; }
            public string error_description { get; set; }
        }

        public class XstsToken
        {
            public string Token { get; set; }
            public DisplayClaimsData DisplayClaims { get; set; }

            public class DisplayClaimsData
            {
                public List<Dictionary<string, string>> xui { get; set; }
            }

            public string Claim(string name)
            {
                var xui = DisplayClaims?.xui;
                if (xui == null || xui.Count == 0)
                {
                    return null;
                }

                return xui[0].TryGetValue(name, out var value) ? value : null;
            }

            public string AuthorizationHeader => $"XBL3.0 x={Claim("uhs")};{Token}";
        }

        public async Task<DeviceCode> StartDeviceCodeAsync()
        {
            var resp = await _http.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "scope", Scope }
            })).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new Exception($"Microsoft refused the device code request ({(int)resp.StatusCode}): {text}");
            }

            return Serialization.FromJson<DeviceCode>(text);
        }

        /// <summary>Polls until the user approves the code. Saves the refresh token.</summary>
        public async Task CompleteDeviceCodeAsync(DeviceCode code, CancellationToken cancel)
        {
            var interval = Math.Max(code.interval, 5);
            var deadline = DateTime.UtcNow.AddSeconds(code.expires_in > 0 ? code.expires_in : 900);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancel).ConfigureAwait(false);
                var token = await RequestTokenAsync(new Dictionary<string, string>
                {
                    { "client_id", ClientId },
                    { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" },
                    { "device_code", code.device_code }
                }).ConfigureAwait(false);

                if (token.access_token != null)
                {
                    SaveRefreshToken(token.refresh_token);
                    return;
                }

                switch (token.error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = Math.Min(interval + 5, 30);
                        continue;
                    case "authorization_declined":
                    case "access_denied":
                        throw new Exception("Sign-in was declined.");
                    case "expired_token":
                        throw new Exception("The sign-in code expired. Try again.");
                    default:
                        throw new Exception($"Sign-in failed: {token.error} {token.error_description}");
                }
            }

            throw new Exception("The sign-in code expired. Try again.");
        }

        /// <summary>Refreshes the Microsoft token and returns XSTS tokens for each relying party.</summary>
        public async Task<Dictionary<string, XstsToken>> GetXstsTokensAsync(params string[] relyingParties)
        {
            var refreshToken = LoadRefreshToken();
            if (refreshToken == null)
            {
                throw new NotAuthenticatedException();
            }

            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "scope", Scope }
            }).ConfigureAwait(false);

            if (token.access_token == null)
            {
                logger.Warn($"Refresh token rejected: {token.error} {token.error_description}");
                if (token.error == "invalid_grant")
                {
                    SignOut();
                    throw new NotAuthenticatedException();
                }

                throw new Exception($"Microsoft token refresh failed: {token.error}");
            }

            if (!string.IsNullOrEmpty(token.refresh_token))
            {
                SaveRefreshToken(token.refresh_token);
            }

            var userToken = await PostXblAsync<XstsToken>(UserAuthUrl, new Dictionary<string, object>
            {
                { "Properties", new Dictionary<string, object>
                    {
                        { "AuthMethod", "RPS" },
                        { "SiteName", "user.auth.xboxlive.com" },
                        { "RpsTicket", "d=" + token.access_token }
                    }
                },
                { "RelyingParty", "http://auth.xboxlive.com" },
                { "TokenType", "JWT" }
            }).ConfigureAwait(false);

            var result = new Dictionary<string, XstsToken>();
            foreach (var rp in relyingParties)
            {
                result[rp] = await PostXblAsync<XstsToken>(XstsUrl, new Dictionary<string, object>
                {
                    { "Properties", new Dictionary<string, object>
                        {
                            { "SandboxId", "RETAIL" },
                            { "UserTokens", new[] { userToken.Token } }
                        }
                    },
                    { "RelyingParty", rp },
                    { "TokenType", "JWT" }
                }).ConfigureAwait(false);
            }

            return result;
        }

        private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form)
        {
            var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form)).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return Serialization.FromJson<TokenResponse>(text) ?? new TokenResponse { error = $"http_{(int)resp.StatusCode}" };
        }

        private async Task<T> PostXblAsync<T>(string url, object body) where T : class
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Add("x-xbl-contract-version", "1");
                req.Headers.Add("Accept", "application/json");
                req.Content = new StringContent(Serialization.ToJson(body), Encoding.UTF8, "application/json");
                var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var xerr = resp.Headers.TryGetValues("x-err", out var values) ? string.Join(",", values) : "";
                    throw new Exception($"Xbox Live auth failed at {url} ({(int)resp.StatusCode}) {xerr} {text}");
                }

                return Serialization.FromJson<T>(text);
            }
        }

        private void SaveRefreshToken(string refreshToken)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_refreshTokenPath));
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_refreshTokenPath, data);
        }

        private string LoadRefreshToken()
        {
            if (!File.Exists(_refreshTokenPath))
            {
                return null;
            }

            try
            {
                var data = ProtectedData.Unprotect(File.ReadAllBytes(_refreshTokenPath), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception e)
            {
                logger.Error(e, "Could not read the saved Xbox sign-in.");
                return null;
            }
        }
    }

    public class NotAuthenticatedException : Exception
    {
        public NotAuthenticatedException() : base("Not signed in to Xbox. Sign in from the Xbox Owned Library settings.")
        {
        }
    }
}
