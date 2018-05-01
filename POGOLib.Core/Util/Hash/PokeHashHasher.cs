using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using POGOLib.Official.Exceptions;
using POGOLib.Official.Util.Encryption.PokeHash;
using POGOLib.Official.Util.Hash.PokeHash;
using POGOProtos.Networking.Envelopes;

namespace POGOLib.Official.Util.Hash
{
    /// <summary>
    ///     This is the <see cref="IHasher"/> which uses the API
    ///     provided by https://www.pokefarmer.com/. If you want
    ///     to buy an API key, go to this url.
    ///     https://talk.pogodev.org/d/51-api-hashing-service-by-pokefarmer
    /// 
    ///     Android version: 0.101.1
    ///     IOS version: 1.71.1
    /// </summary>
    public class PokeHashHasher : IHasher, IDisposable
    {
        private readonly List<PokeHashAuthKey> _authKeys;
        private readonly List<PokeHashAuthHasher> _authHashers
            = new List<PokeHashAuthHasher> { new PokeHashAuthHasher("PH", new Uri("http://hash.goman.io/")) };

        private readonly Semaphore _keySelection;

        public Version PokemonVersion { get; } = new Version("0.101.1");

        // NOTE: Warning: Sotmetimes, this value is not the same that API version, so we need know it for each new API version.
        public uint AppVersion { get; } = 10101;

        //Not OK all uk25 is for 9100 need update and maybe new encrypt
        //Not Works
        public long Unknown25 { get; } = unchecked((long)0xF522F8878F08FFD6);

        /// <summary>
        ///     Initializes the <see cref="PokeHashHasher"/>.
        /// </summary>
        /// <param name="authKey">The PokeHash authkey obtained from https://talk.pogodev.org/d/51-api-hashing-service-by-pokefarmer. </param>
        public PokeHashHasher(string authKey) : this(new[] { authKey })
        {

        }

        /// <summary>
        ///     Get Hash server of current key the <see cref="PokeHashHasher"/>.
        /// </summary>
        /// <param name="key">The PokeHash authkeys obtained from Hash Server. </param>
        private Uri GetHashUri(string key)
        {
            foreach (var hasher in _authHashers)
            {
                if (key.StartsWith(hasher.pattern, StringComparison.Ordinal))
                    return hasher.uri;
            }
            return Configuration.HasherUrl; ;
        }

        /// <summary>
        ///     Initializes the <see cref="PokeHashHasher"/>.
        /// </summary>
        /// <param name="authKeys">The PokeHash authkeys obtained from https://talk.pogodev.org/d/51-api-hashing-service-by-pokefarmer. </param>
        public PokeHashHasher(string[] authKeys)
        {
            if (authKeys.Length < 1)
                throw new PokeHashException($"{nameof(authKeys)} may not be empty.");

            _authKeys = new List<PokeHashAuthKey>();

            // We don't want any duplicate keys.
            foreach (var authKey in authKeys)
            {
                var pokeHashAuthKey = new PokeHashAuthKey(authKey);
                if (_authKeys.Contains(pokeHashAuthKey))
                    throw new PokeHashException($"The auth key '{authKey}' is a duplicate.");
                _authKeys.Add(pokeHashAuthKey);
            }

            var rnd = new Random();
            _authHashers = _authHashers.OrderBy(item => rnd.Next()).ToList();

            if (!Configuration.IgnoreHashSemafore)
                _keySelection = new Semaphore(1, 1);
        }

        public async Task<HashData> GetHashDataAsync(RequestEnvelope requestEnvelope, Signature signature, byte[] locationBytes, byte[][] requestsBytes, byte[] serializedTicket)
        {
            var requestData = new PokeHashRequest
            {
                Timestamp = signature.Timestamp,
                Latitude = requestEnvelope.Latitude,
                Longitude = requestEnvelope.Longitude,
                Altitude = requestEnvelope.Accuracy, // Accuracy actually is altitude
                AuthTicket = serializedTicket,
                SessionData = signature.SessionHash.ToByteArray(),
                Requests = new List<byte[]>(requestsBytes)
            };

            var requestContent = new StringContent(JsonConvert.SerializeObject(requestData), Encoding.UTF8, "application/json");

            using (var response = await PerformRequest(requestContent))
            {
                if (response == null)
                {
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();

                string message = String.Empty;

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var responseData = JsonConvert.DeserializeObject<PokeHashResponse>(responseContent);

                        return new HashData
                        {
                            LocationAuthHash = responseData.LocationAuthHash,
                            LocationHash = responseData.LocationHash,
                            RequestHashes = responseData.RequestHashes
                                .Select(x => (ulong)x)
                                .ToArray()
                        };

                    case HttpStatusCode.NotFound:
                        message = $"Hashing endpoint not found!";
                        break;

                    case HttpStatusCode.BadRequest:
                        message = "Bad request sent to the hashing server!";
                        break;

                    case HttpStatusCode.Unauthorized:
                        message = "The auth key supplied for PokeHash was invalid.";
                        break;

                    case (HttpStatusCode)429:
                        //Re-send
                        HashData resend = null;
                        resend = await GetHashDataAsync(requestEnvelope, signature, locationBytes, requestsBytes, serializedTicket);

                        if (resend == null)
                            message = $"Your request has been limited. {response}";
                        else
                            return resend;
                        break;

                    default:
                        message = $"We received an unknown HttpStatusCode ({response.StatusCode})..";
                        break;
                }

                if (!String.IsNullOrEmpty(message))// retryCount == 10)
                {
                    // TODO: Find a better way to let the developer know of these issues.
                    message = $"[PokeHash]: {message}";
                    Dispose();
                    throw new PokeHashException(message);
                }
            }
            return null;
        }

        private async Task<HttpResponseMessage> PerformRequest(HttpContent requestContent)
        {
            //Retries
            int retries = 3;
            HttpResponseMessage response = null;

            // Key selection
            if (!Configuration.IgnoreHashSemafore)
                _keySelection.WaitOne();

            try
            {
                PokeHashAuthKey authKey;
                do
                {
                    try
                    {
                        var availableKeys = _authKeys.Where(x => x.Requests < x.MaxRequestCount).ToArray();
                        if (availableKeys.Length > 0)
                        {
                            authKey = availableKeys.First();
                            authKey.Requests += 1;
                        }
                        else
                        {
                            authKey = _authKeys
                                .OrderBy(x => x.RatePeriodEnd)
                                .First();
                            // Rate limit is over, so reset requests.
                            authKey.Requests = 0;
                        }

                        // Add hashkey
                        requestContent.Headers.Add("X-AuthToken", authKey.AuthKey);

                        // Initialize HttpClient.
                        using (var _httpClient = new HttpClient
                        {
                            BaseAddress = GetHashUri(authKey.AuthKey)
                        })
                        {
                            _httpClient.DefaultRequestHeaders.Clear();
                            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            _httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("POGOLib.Core (https://github.com/Furtif/POGOLib)");
                            response = await _httpClient.PostAsync(Configuration.HashEndpoint, requestContent);
                            _httpClient.Dispose();
                        };

                        // Parse headers
                        int maxRequestCount;
                        int rateRequestsRemaining;
                        int ratePeriodEndSeconds;

                        IEnumerable<string> maxRequestsValue;
                        IEnumerable<string> requestsRemainingValue;
                        IEnumerable<string> ratePeriodEndValue;
                        if (response.Headers.TryGetValues("X-MaxRequestCount", out maxRequestsValue) &&
                            response.Headers.TryGetValues("X-RateRequestsRemaining", out requestsRemainingValue) &&
                            response.Headers.TryGetValues("X-RatePeriodEnd", out ratePeriodEndValue))
                        {
                            if (!int.TryParse(maxRequestsValue.First(), out maxRequestCount) ||
                                !int.TryParse(requestsRemainingValue.First(), out rateRequestsRemaining) ||
                                !int.TryParse(ratePeriodEndValue.FirstOrDefault(), out ratePeriodEndSeconds))
                            {
                                Dispose();
                                throw new PokeHashException("Failed parsing pokehash response header values.");
                            }
                        }
                        else
                        {
                            Dispose();
                            throw new PokeHashException("Failed parsing pokehash response headers.");
                        }

                        // Use parsed headers
                        if (!authKey.IsInitialized)
                        {
                            authKey.MaxRequestCount = maxRequestCount;
                            authKey.Requests = authKey.MaxRequestCount - rateRequestsRemaining;
                            authKey.IsInitialized = true;
                        }

                        var ratePeriodEnd = TimeUtil.GetDateTimeFromSeconds(ratePeriodEndSeconds);
                        if (ratePeriodEnd > authKey.RatePeriodEnd)
                        {
                            authKey.RatePeriodEnd = ratePeriodEnd;
                        }

                        if (response == null)
                        {
                            Dispose();
                            throw new PokeHashException("Missed hash response Data");
                        }
                        return response;
                    }
                    catch
                    {
                        //Null
                    }

                    await Task.Delay(1000);
                    retries--;
                } while (retries > 0);
            }
            finally
            {
                if (!Configuration.IgnoreHashSemafore)
                    _keySelection.Release();
            }
            return response;
        }

        public byte[] GetEncryptedSignature(byte[] signatureBytes, uint timestampSinceStartMs)
        {
            return PCryptPokeHash.Encrypt(signatureBytes, timestampSinceStartMs);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        internal void Dispose(bool disposing)
        {
            if (!disposing) return;
            if (!Configuration.IgnoreHashSemafore)
                _keySelection?.Dispose();
        }
    }
}
