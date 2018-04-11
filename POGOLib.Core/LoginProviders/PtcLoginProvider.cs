using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using POGOLib.Official.Net.Authentication.Data;
using System.Net;
using POGOLib.Official.Exceptions;

namespace POGOLib.Official.LoginProviders
{

    /// <summary>
    /// The <see cref="ILoginProvider"/> for Pokemon Trainer Club.
    /// Use this if you want to authenticate to PokemonGo using a Pokemon Trainer Club account.
    /// </summary>
    public class PtcLoginProvider : ILoginProvider
    {
        private readonly string _username;
        private readonly string _password;
        private readonly IWebProxy _proxy;

        public PtcLoginProvider(string username, string password, IWebProxy proxy = null)
        {
            _username = username;
            _password = password;
            _proxy = proxy;
        }

        /// <summary>
        /// The unique identifier of the <see cref="PtcLoginProvider"/>.
        /// </summary>
        public string ProviderId { get { return "ptc"; } }

        /// <summary>
        /// The unique identifier of the user trying to authenticate using the <see cref="PtcLoginProvider"/>.
        /// </summary>
        public string UserId { get { return _username; } }


        /// <summary>
        /// Retrieves an <see cref="AccessToken"/> by logging into the Pokemon Trainer Club website.
        /// </summary>
        /// <returns>Returns an <see cref="AccessToken"/>.</returns>
        public async Task<AccessToken> GetAccessToken(string useragent, string language)
        {
            using (var httpClientHandler = new HttpClientHandler())
            {
                httpClientHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                httpClientHandler.AllowAutoRedirect = false;
                httpClientHandler.UseProxy = _proxy != null;
                httpClientHandler.Proxy = _proxy;
                httpClientHandler.UseCookies = true;
                httpClientHandler.CookieContainer = new CookieContainer();
                using (var httpClient = new HttpClient(httpClientHandler, true))
                {
                    httpClient.DefaultRequestHeaders.Host = "sso.pokemon.com";
                    httpClient.DefaultRequestHeaders.Connection.TryParseAdd("keep-alive");
                    httpClient.DefaultRequestHeaders.Accept.TryParseAdd("*/*");
                    httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd($"pokemongo/1 {useragent}");
                    httpClient.DefaultRequestHeaders.AcceptLanguage.TryParseAdd(language);
                    httpClient.DefaultRequestHeaders.AcceptEncoding.TryParseAdd("gzip-deflate");
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Unity-Version", "2017.1.2f1"); //"5.5.1f1";//"5.6.1f1";
                    httpClient.Timeout.Add(new TimeSpan(0, 10, 0));
                    var logout = await LogOut(httpClient);
                    var loginData = await GetLoginData(httpClient, language.Replace("-", "_"));
                    var ticket = await PostLogin(httpClient, _username, _password, loginData, httpClientHandler.CookieContainer, language.Replace("-", "_"));
                    var accessToken = await PostLoginOauth(httpClient, ticket);
                    accessToken.Username = _username;
                    var profile = await GetProfile(httpClient, accessToken.Token, language.Replace("-", "_"));
                    return accessToken;
                }
            }
        }

        private async Task<string> LogOut(HttpClient httpClient)
        {
            var uriBuilder = new UriBuilder("https://sso.pokemon.com/sso/logout")
            {
                Port = -1,
                Query = await
             new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"service", "https://sso.pokemon.com/sso/oauth2.0/callbackAuthorize" }
                }).ReadAsStringAsync()
            };
            var loginDataResponse = await httpClient.GetAsync(uriBuilder.ToString());
            //if (loginDataResponse.StatusCode ==  HttpStatusCode.OK){
            uriBuilder = new UriBuilder(loginDataResponse.Headers.Location);
            var content = await loginDataResponse.Content.ReadAsStringAsync();
            return content;
            //}
            //throw new PtcLoginException("Pokemon Trainer Club gave error(s): '");
        }

        /// <summary>
        /// Responsible for retrieving login parameters for <see cref="PostLogin" />.
        /// </summary>
        /// <param name="httpClient">An initialized <see cref="HttpClient" />.</param>
        /// <returns><see cref="LoginData" /> for <see cref="PostLogin" />.</returns>
        private async Task<LoginData> GetLoginData(HttpClient httpClient, string language)
        {
            var uriBuilder = new UriBuilder("https://sso.pokemon.com/sso/login")
            {
                Port = -1,
                //TODO: use selected locale information
                Query = await new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"service", "https://sso.pokemon.com/sso/oauth2.0/callbackAuthorize" },
                    {"locale",  language}
                }).ReadAsStringAsync()
            };
            var loginDataResponse = await httpClient.GetAsync(uriBuilder.ToString());
            if (loginDataResponse.StatusCode == HttpStatusCode.OK)
            {
                var content = await loginDataResponse.Content.ReadAsStringAsync();
                var loginData = JsonConvert.DeserializeObject<LoginData>(content);
                return loginData;
            }
            throw new PtcLoginException("Pokemon Trainer Club gave error(s): '");
        }

        /// <summary>
        /// Responsible for submitting the login request.
        /// </summary>
        /// <param name="httpClient"></param>
        /// <param name="username">The user's PTC username.</param>
        /// <param name="password">The user's PTC password.</param>
        /// <param name="loginData"><see cref="LoginData" /> taken from PTC website using <see cref="GetLoginData" />.</param>
        /// <param name = "cookieContainer"> containter where the cookies are stored</param>
        /// <returns></returns>
        private async Task<string> PostLogin(HttpClient httpClient, string username, string password, LoginData loginData, CookieContainer cookieContainer, string language)
        {
            var uriBuilder = new UriBuilder("https://sso.pokemon.com/sso/login")
            {
                Port = -1,
                Query = await new FormUrlEncodedContent(new Dictionary<string, string> {
                     {"service", "https://sso.pokemon.com/sso/oauth2.0/callbackAuthorize"} ,
                     {"locale", language}
                    }).ReadAsStringAsync()
            };
            var loginResponse =
                await httpClient.PostAsync(uriBuilder.ToString(), new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"lt", loginData.Lt},
                    {"execution", loginData.Execution},
                    {"_eventId", "submit"},
                    {"username", username},
                    {"password", password},
                    { "locale", language}
                }));

            if (loginResponse.StatusCode == HttpStatusCode.Found && loginResponse.Headers.Location != null)
            {
                var locationQuery = loginResponse.Headers.Location.Query;
                var ticketStartPosition = locationQuery.IndexOf("=", StringComparison.Ordinal) + 1;
                return locationQuery.Substring(ticketStartPosition, locationQuery.Length - ticketStartPosition);
            }

            var loginResponseDataRaw = await loginResponse.Content.ReadAsStringAsync();
            if (!loginResponseDataRaw.Contains("{"))
            {
                var locationQuery = loginResponse.Headers.Location.Query;
                var ticketStartPosition = locationQuery.IndexOf("=", StringComparison.Ordinal) + 1;
                return locationQuery.Substring(ticketStartPosition, locationQuery.Length - ticketStartPosition);
            }

            var loginResponseData = JObject.Parse(loginResponseDataRaw);
            var loginResponseErrors = (JArray)loginResponseData["errors"];
            var parsedErrors = WebUtility.HtmlDecode(string.Join(",", loginResponseErrors));
            throw new PtcLoginException("Pokemon Trainer Club gave error(s): '" + parsedErrors + "'");
        }

        /// <summary>
        /// Responsible for finishing the oauth login request.
        /// </summary>
        /// <param name="httpClient"></param>
        /// <param name="ticket"></param>
        /// <returns></returns>
        private async Task<AccessToken> PostLoginOauth(HttpClient httpClient, string ticket)
        {
            var loginResponse =
                await httpClient.PostAsync(Constants.LoginOauthUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"client_id", "mobile-app_pokemon-go"},
                    {"redirect_uri", "https://www.nianticlabs.com/pokemongo/error"},
                    {"client_secret", "w8ScCUXJQc6kXKw8FiOhd8Fixzht18Dq3PEVkUCP5ZPxtgyWsbTvWHFLm2wNY0JR"},
                    {"grant_type", "refresh_token"},
                    {"code", ticket}
                }));

            var loginResponseDataRaw = await loginResponse.Content.ReadAsStringAsync();
            if (loginResponse.StatusCode == HttpStatusCode.OK)
            {
                var oAuthData = Regex.Match(loginResponseDataRaw, "access_token=(?<accessToken>.*?)&expires=(?<expires>\\d+)");
                if (!oAuthData.Success)
                    throw new PtcLoginException($"Couldn't verify the OAuth login response data '" + loginResponseDataRaw + "'.");

                return new AccessToken
                {
                    Token = oAuthData.Groups["accessToken"].Value,
                    Expiry = DateTime.UtcNow.AddSeconds(int.Parse(oAuthData.Groups["expires"].Value)),
                    ProviderID = ProviderId
                };
            }

            throw new PtcLoginException("Pokemon Trainer Club gave error(s): '" + loginResponse.StatusCode + "'");

            /*
            var loginResponseData = JObject.Parse(loginResponseDataRaw);
            var loginResponseErrors = (JArray)loginResponseData["errors"];
            var parsedErrors = WebUtility.HtmlDecode(string.Join(",", loginResponseErrors));
            throw new PtcLoginException("Pokemon Trainer Club gave error(s): '" + parsedErrors + "'");
            */
        }

        private async Task<string> GetProfile(HttpClient httpClient, string token, string language)
        {
            var uriBuilder = new UriBuilder("https://sso.pokemon.com/sso/oauth2.0/profile")
            {
                Port = -1,
                //TODO: use selected locale information
                Query = await
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"access_token", token },
                    {"client_id", "mobile-app_pokemon-go" },
                    {"locale",  language}
                }).ReadAsStringAsync()
            };
            var loginDataResponse = await httpClient.GetAsync(uriBuilder.ToString());
            if (loginDataResponse.StatusCode == HttpStatusCode.OK)
            {
                var content = await loginDataResponse.Content.ReadAsStringAsync();
                return content;
            }
            throw new PtcLoginException("Pokemon Trainer Club gave error(s): '" + loginDataResponse.StatusCode);
        }
    }
}
