using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using POGOLib.Official.Exceptions;
using POGOLib.Official.Logging;
using POGOLib.Official.LoginProviders;
using POGOLib.Official.Net.Authentication.Data;
using POGOLib.Official.Net.Captcha;
using POGOLib.Official.Pokemon;
using POGOLib.Official.Util.Device;
using POGOProtos.Data;
using POGOProtos.Settings;
using POGOProtos.Networking.Requests.Messages;
using POGOProtos.Networking.Responses;
using System.Collections.Generic;
using POGOLib.Official.Extensions;

namespace POGOLib.Official.Net
{
    /// <summary>
    /// This is an authenticated <see cref="Session" /> with PokémonGo that handles everything between the developer and PokémonGo.
    /// </summary>
    public class Session : IDisposable
    {
        private SessionState _state;

        private bool _incenseUsed;

        private bool _luckyEggsUsed;

        private bool _manageresources;

        /// <summary>
        /// This is the <see cref="HeartbeatDispatcher" /> which is responsible for retrieving events and updating gps location.
        /// </summary>
        private readonly HeartbeatDispatcher Heartbeat;

        /// <summary>
        /// This is the <see cref="RpcClient" /> which is responsible for all communication between us and PokémonGo.
        /// Only use this if you know what you are doing.
        /// </summary>
        public readonly RpcClient RpcClient;

        public readonly Logger Logger;

        private static readonly string[] ValidLoginProviders = { "ptc", "google" };

        /// <summary>
        /// Stores data like assets and item templates. Defaults to an in-memory cache, but can be implemented as writing to disk by the platform
        /// </summary>
        // public IDataCache DataCache { get; set; } = new MemoryDataCache();
        // public Templates Templates { get; private set; }

        internal Session(ILoginProvider loginProvider, AccessToken accessToken, GeoCoordinate geoCoordinate, DeviceWrapper deviceWrapper, GetPlayerMessage.Types.PlayerLocale playerLocale)
        {
            if (!ValidLoginProviders.Contains(loginProvider.ProviderId))
            {
                throw new ArgumentException("LoginProvider ID must be one of the following: " + string.Join(", ", ValidLoginProviders));
            }
            Logger = new Logger();

            State = SessionState.Stopped;

            Device = deviceWrapper;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = Device.Proxy != null,
                Proxy = Device.Proxy
            };

            HttpClient = new HttpClient(handler);
            HttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(Constants.ApiUserAgent);
            HttpClient.DefaultRequestHeaders.ExpectContinue = false;

            AccessToken = accessToken;

            if (IsValidAccessToken())
            {
                OnAccessTokenUpdated();
            }
            else
                throw new SessionStateException("INVALID AUTH TOKEN");

            LoginProvider = loginProvider;
            Player = new Player(this, geoCoordinate, playerLocale);
            Map = new Map(this);
            Templates = new Templates(this);
            RpcClient = new RpcClient(this);

            if (Configuration.EnableHeartbeat)
                Heartbeat = new HeartbeatDispatcher(this);
        }

        /// <summary>
        /// Gets Incense active of the <see cref="Session" />.
        /// </summary>
        public bool IncenseUsed
        {
            get { return _incenseUsed; }
            internal set
            {
                _incenseUsed = value;
            }
        }

        /// <summary>
        /// Gets LukyEggs active of the <see cref="Session" />.
        /// </summary>
        public bool LuckyEggsUsed
        {
            get { return _luckyEggsUsed; }
            internal set
            {
                _luckyEggsUsed = value;
            }
        }

        /// <summary>
        /// Gets ManageRessources active of the <see cref="Session" />.
        /// </summary>
        public bool ManageRessources
        {
            get { return _manageresources; }
            set
            {
                _manageresources = value;
            }
        }

        /// <summary>
        /// Gets the <see cref="SessionState"/> of the <see cref="Session"/>.
        /// </summary>
        public SessionState State
        {
            get { return _state; }
            private set
            {
                _state = value;

                Logger.Debug($"Session state was set to {_state}.");
            }
        }

        internal void SetTemporalBan()
        {
            State = SessionState.TemporalBanned;
            OnTemporalBanReceived();
            if (State != SessionState.Stopped)
                Shutdown();
        }

        /// <summary>
        /// Gets the <see cref="Random"/> of the <see cref="Session"/>.
        /// </summary>
        internal Random Random { get; private set; } = new Random();

        /// <summary>
        /// Gets the <see cref="DeviceWrapper"/> used by <see cref="RpcEncryption"/>.
        /// </summary>
        public DeviceWrapper Device { get; private set; }

        /// <summary>
        /// Gets the <see cref="HttpClient"/> of the <see cref="Session"/>.
        /// </summary>
        internal HttpClient HttpClient { get; }

        /// <summary>
        /// Gets the <see cref="ILoginProvider"/> used to obtain an <see cref="AccessToken"/>.
        /// </summary>
        private ILoginProvider LoginProvider { get; }

        /// <summary>
        ///  Gets the <see cref="AccessToken"/> of the <see cref="Session" />.
        /// </summary>
        public AccessToken AccessToken { get; private set; }

        /// <summary>
        /// Gets the <see cref="Player"/> of the <see cref="Session" />.
        /// </summary>
        public Player Player { get; private set; }

        /// <summary>
        /// Gets the <see cref="Map"/> of the <see cref="Session" />.
        /// </summary>
        public Map Map { get; }

        /// <summary>
        /// Gets the <see cref="GlobalSettings"/> of the <see cref="Session" />.
        /// </summary>
        public GlobalSettings GlobalSettings { get; internal set; }

        /// <summary>
        /// Gets the hash of the <see cref="GlobalSettings"/>.
        /// </summary>
        internal string GlobalSettingsHash { get; set; } = string.Empty;

        private Semaphore ReauthenticateMutex { get; } = new Semaphore(1, 1);

        public Templates Templates { get; private set; }

        public async Task<bool> StartupAsync()
        {
            if (State != SessionState.Stopped)
            {
                throw new SessionStateException("The session has already been started.");
            }

            if (!Configuration.IgnoreHashVersion)
            {
                await CheckHasherVersion();
            }

            State = SessionState.Started;

            if (!await RpcClient.StartupAsync())
            {
                return false;
            }

            if (Configuration.EnableHeartbeat)
                await Heartbeat.StartDispatcherAsync();

            return true;
        }

        public void Pause()
        {
            if (State != SessionState.Started &&
                State != SessionState.Resumed && State != SessionState.TemporalBanned)
            {
                throw new SessionStateException("The session is not running.");
            }

            State = SessionState.Paused;

            if (Configuration.EnableHeartbeat)
                Heartbeat.StopDispatcher();
        }

        public async Task ResumeAsync()
        {
            if (State != SessionState.Paused)
            {
                throw new SessionStateException("The session is not paused.");
            }

            State = SessionState.Resumed;

            if (Configuration.EnableHeartbeat)
                await Heartbeat.StartDispatcherAsync();
        }

        public void Shutdown()
        {
            if (State == SessionState.Stopped)
            {
                throw new SessionStateException("The session has already been stopped.");
            }

            if (State != SessionState.TemporalBanned)
                State = SessionState.Stopped;

            if (Configuration.EnableHeartbeat)
                Heartbeat.StopDispatcher();
        }

        /// <summary>
        /// Checks if the current minimal version of PokemonGo matches the version of the <see cref="Configuration.Hasher"/>.
        /// Throws an exception if there is a version mismatch.
        /// </summary>
        /// <returns></returns>
        public async Task CheckHasherVersion()
        {
            using (var checkHttpClient = new HttpClient())
            {
                checkHttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd(Device.UserAgent);

                var clientVersionRaw = await checkHttpClient.GetByteArrayAsync(Constants.VersionUrl);
                var clientVersion = ClientVersion.Parser.ParseFrom(clientVersionRaw);

                var pogoVersion = new Version(clientVersion.MinVersion);
                var result = Configuration.Hasher.PokemonVersion.CompareTo(pogoVersion);
                if (result < 0)
                {
                    throw new HashVersionMismatchException($"The version of the {nameof(Configuration.Hasher)} ({Configuration.Hasher.PokemonVersion}) does not match the minimal API version of PokemonGo ({pogoVersion}). Set 'Configuration.IgnoreHashVersion' to true if you want to disable the version check.");
                }

                checkHttpClient.Dispose();
            }
        }

        /// <summary>
        /// Ensures the <see cref="Session" /> check if is valid access token.
        /// </summary>
        private bool IsValidAccessToken()
        {
            if (AccessToken == null || string.IsNullOrEmpty(AccessToken.Token) || AccessToken.IsExpired)
                return false;

            return true;
        }

        /// <summary>
        /// Ensures the <see cref="Session" /> gets valid access token.
        /// </summary>
        internal async Task<AccessToken> GetValidAccessToken(bool forceRefresh = false)
        {
            try
            {
                ReauthenticateMutex.WaitOne();

                if (forceRefresh)
                {
                    AccessToken.Expire();
                }

                if (IsValidAccessToken())
                    return AccessToken;

                await Reauthenticate();
                return AccessToken;
            }
            finally
            {
                ReauthenticateMutex.Release();
            }
        }

        /// <summary>
        /// Ensures the <see cref="Session" /> gets reauthenticated, no matter how long it takes.
        /// </summary>
        private async Task Reauthenticate()
        {
            var tries = 0;

            while (!IsValidAccessToken())
            {
                try
                {
                    string language = this.Player.PlayerLocale.Language + "-" + this.Player.PlayerLocale.Country;
                    AccessToken = await LoginProvider.GetAccessToken(this.Device.UserAgent, language);
                    if (LoginProvider is PtcLoginProvider)
                        Logger.Debug("Authenticated through PTC.");
                    else
                        Logger.Debug("Authenticated through Google.");
                }
                catch (PtcLoginException ex)
                {
                    if (ex.Message.Contains("15 minutes")) throw new PtcLoginException(ex.Message);
                    throw new PtcLoginException($"Reauthenticate exception was catched: {ex}");
                }
                catch (GoogleLoginException ex)
                {
                    if (ex.Message.Contains("You have to log into a browser")) throw new GoogleLoginException(ex.Message);
                    throw new GoogleLoginException($"Reauthenticate exception was catched: {ex}");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Reauthenticate exception was catched: {ex}");
                }
                finally
                {
                    if (tries == 5)
                    {
                        throw new SessionStateException("Error refreshing access token.");
                    }

                    ++tries;

                    if (!IsValidAccessToken())
                    {
                        var sleepSeconds = Math.Min(60, tries * 5);
                        Logger.Error($"Reauthentication failed, trying again in {sleepSeconds} seconds.");
                        await Task.Delay(TimeSpan.FromMilliseconds(sleepSeconds * 1000));
                    }
                    else
                    {
                        OnAccessTokenUpdated();
                    }
                }
            }

            if (!IsValidAccessToken())
            {
                throw new SessionStateException("Error refreshing access token.");
            }
        }

        #region Events
        internal void OnTemporalBanReceived()
        {
            TemporalBanReceived?.Invoke(this, EventArgs.Empty);
        }

        internal void OnAccessTokenUpdated()
        {
            AccessTokenUpdated?.Invoke(this, EventArgs.Empty);
        }

        internal void OnInventoryUpdate()
        {
            InventoryUpdate?.Invoke(this, EventArgs.Empty);
        }

        internal void OnMapUpdate()
        {
            MapUpdate?.Invoke(this, EventArgs.Empty);
        }

        internal void OnCaptchaReceived(string url)
        {
            CaptchaReceived?.Invoke(this, new CaptchaEventArgs(url));
        }

        internal void OnHatchedEggsReceived(GetHatchedEggsResponse getHatchedEggsResponse)
        {
            HatchedEggsReceived?.Invoke(this, new GetHatchedEggsResponse(getHatchedEggsResponse));
        }

        internal void OnCheckAwardedBadgesReceived(CheckAwardedBadgesResponse checkAwardedBadgesResponse)
        {
            CheckAwardedBadgesReceived?.Invoke(this, new CheckAwardedBadgesResponse(checkAwardedBadgesResponse));
        }

        internal void OnItemTemplatesReceived(List<DownloadItemTemplatesResponse.Types.ItemTemplate> itemtemplates)
        {
            ItemTemplatesUpdated?.Invoke(this, new List<DownloadItemTemplatesResponse.Types.ItemTemplate>(itemtemplates));
        }

        internal void OnAssetDigestReceived(List<AssetDigestEntry> assetdigest)
        {
            AssetDigestUpdated?.Invoke(this, new List<AssetDigestEntry>(assetdigest));
        }

        internal void OnUrlsReceived(List<DownloadUrlEntry> urls)
        {
            UrlsUpdated?.Invoke(this, new List<DownloadUrlEntry>(urls));
        }

        internal void OnRemoteConfigVersionReceived(DownloadRemoteConfigVersionResponse downloadRemoteConfigVersionResponse)
        {
            RemoteConfigVersionUpdated?.Invoke(this, new DownloadRemoteConfigVersionResponse(downloadRemoteConfigVersionResponse));
        }

        internal void OnBuddyWalked(GetBuddyWalkedResponse buddyWalked)
        {
            BuddyWalked?.Invoke(this, new GetBuddyWalkedResponse(buddyWalked));
        }

        internal void OnInboxNotification(GetInboxResponse inboxdata)
        {
            InboxDataReceived?.Invoke(this, new GetInboxResponse(inboxdata));
        }

        public event EventHandler<GetInboxResponse> InboxDataReceived;

        public event EventHandler<EventArgs> TemporalBanReceived;

        public event EventHandler<DownloadRemoteConfigVersionResponse> RemoteConfigVersionUpdated;

        public event EventHandler<List<POGOProtos.Data.DownloadUrlEntry>> UrlsUpdated;

        public event EventHandler<List<POGOProtos.Data.AssetDigestEntry>> AssetDigestUpdated;

        public event EventHandler<List<DownloadItemTemplatesResponse.Types.ItemTemplate>> ItemTemplatesUpdated;

        public event EventHandler<EventArgs> AccessTokenUpdated;

        public event EventHandler<EventArgs> InventoryUpdate;

        public event EventHandler<EventArgs> MapUpdate;

        public event EventHandler<GetHatchedEggsResponse> HatchedEggsReceived;

        public event EventHandler<GetBuddyWalkedResponse> BuddyWalked;

        public event EventHandler<CheckAwardedBadgesResponse> CheckAwardedBadgesReceived;

        /// <summary>
        /// If you have successfully solved the captcha using VerifyChallange, 
        /// may be you can resume POGOLib by using <see cref="ResumeAsync"/>.
        /// </summary>
        public event EventHandler<CaptchaEventArgs> CaptchaReceived;
        #endregion

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            ReauthenticateMutex?.Dispose();
            RpcClient?.Dispose();
            HttpClient?.Dispose();
        }
    }
}
