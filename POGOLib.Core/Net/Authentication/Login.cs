using System.Threading.Tasks;
using POGOLib.Official.Exceptions;
using POGOLib.Official.Extensions;
using POGOLib.Official.LoginProviders;
using POGOLib.Official.Net.Authentication.Data;
using POGOLib.Official.Util.Device;
using POGOProtos.Networking.Requests.Messages;

namespace POGOLib.Official.Net.Authentication
{
    /// <summary>
    /// Responsible for Authenticating and Re-authenticating the user.
    /// </summary>
    public static class Login
    {
        /// <summary>
        /// Login with a stored <see cref="AccessToken" />.
        /// </summary>
        /// <param name="loginProvider"></param>
        /// <param name="accessToken"></param>
        /// <param name="initialLatitude">The initial latitude you will spawn at after logging into PokémonGo.</param>
        /// <param name="initialLongitude">The initial longitude you will spawn at after logging into PokémonGo.</param>
        /// <param name="deviceWrapper">The <see cref="DeviceWrapper"/> used by the <see cref="Session"/>, keep null if you want a randomly generated <see cref="DeviceWrapper"/>.</param>
        /// <param name = "playerLocale"></param>
        /// <returns></returns>
        public static async Task<Session> GetSession(ILoginProvider loginProvider, AccessToken accessToken, double initialLatitude, double initialLongitude, DeviceWrapper deviceWrapper = null, GetPlayerMessage.Types.PlayerLocale playerLocale = null)
        {
            DeviceWrapper device = deviceWrapper ?? DeviceInfoUtil.GetRandomDevice();
            GetPlayerMessage.Types.PlayerLocale locale = playerLocale ?? new GetPlayerMessage.Types.PlayerLocale { Country = "US", Language = "en", Timezone = "America/New_York" };
            string language = locale.Language + "-" + locale.Country;

            if (accessToken == null || string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
            {
                accessToken = await loginProvider.GetAccessToken(device.UserAgent, language);
                if (accessToken == null || string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
                    throw new SessionStateException($"{nameof(accessToken)} is expired.");
            }

            var session = new Session(loginProvider, accessToken, new GeoCoordinate(initialLatitude, initialLongitude), device, locale);
            session.Logger.Debug("Authenticated from cache.");
            return session;
        }

        /// <summary>
        /// Login through OAuth with PTC / Google.
        /// </summary>
        /// <param name="loginProvider">The OAuth provider you use to authenticate.</param>
        /// <param name="initialLatitude">The initial latitude you will spawn at after logging into PokémonGo.</param>
        /// <param name="initialLongitude">The initial longitude you will spawn at after logging into PokémonGo.</param>
        /// <param name="deviceWrapper">The <see cref="DeviceWrapper"/> used by the <see cref="Session"/>, keep null if you want a randomly generated <see cref="DeviceWrapper"/>.</param>
        /// <param name = "playerLocale"></param>
        /// <returns></returns>
        public static async Task<Session> GetSession(ILoginProvider loginProvider, double initialLatitude, double initialLongitude, DeviceWrapper deviceWrapper = null, GetPlayerMessage.Types.PlayerLocale playerLocale = null)
        {
            DeviceWrapper device = deviceWrapper ?? DeviceInfoUtil.GetRandomDevice();
            GetPlayerMessage.Types.PlayerLocale locale = playerLocale ?? new GetPlayerMessage.Types.PlayerLocale { Country = "US", Language = "en", Timezone = "America/New_York" };
            string language = locale.Language + "-" + locale.Country;
            var session = new Session(loginProvider, await loginProvider.GetAccessToken(device.UserAgent, language), new GeoCoordinate(initialLatitude, initialLongitude), device, locale);
            if (loginProvider is PtcLoginProvider)
                session.Logger.Debug("Authenticated through PTC.");
            else
                session.Logger.Debug("Authenticated through Google.");
            return session;
        }

        /// <summary>
        /// Login with a stored <see cref="AccessToken" />.
        /// </summary>
        /// <param name="loginProvider">The OAuth provider you use to authenticate.</param>
        /// <param name="accessToken">The <see cref="AccessToken"/> you want to re-use.</param>
        /// <param name="coordinate">The initial coordinate you will spawn at after logging into PokémonGo.</param>
        /// <param name="deviceWrapper">The <see cref="DeviceWrapper"/> used by the <see cref="Session"/>, keep null if you want a randomly generated <see cref="DeviceWrapper"/>.</param>
        /// <param name = "playerLocale"></param>
        /// <returns></returns>
        public static async Task<Session> GetSession(ILoginProvider loginProvider, AccessToken accessToken, GeoCoordinate coordinate, DeviceWrapper deviceWrapper = null, GetPlayerMessage.Types.PlayerLocale playerLocale = null)
        {      
            DeviceWrapper device = deviceWrapper ?? DeviceInfoUtil.GetRandomDevice();
            GetPlayerMessage.Types.PlayerLocale locale = playerLocale ?? new GetPlayerMessage.Types.PlayerLocale { Country = "US", Language = "en", Timezone = "America/New_York" };
            string language = locale.Language + "-" + locale.Country;

            if (accessToken == null || string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
            {
                accessToken = await loginProvider.GetAccessToken(device.UserAgent, language);
                if (accessToken == null || string.IsNullOrEmpty(accessToken.Token) || accessToken.IsExpired)
                    throw new SessionStateException($"{nameof(accessToken)} is expired.");
            }

            var session = new Session(loginProvider, accessToken, coordinate, device, locale);
            session.Logger.Debug("Authenticated from cache.");
            if (loginProvider is PtcLoginProvider)
                session.Logger.Debug("Authenticated through PTC.");
            else
                session.Logger.Debug("Authenticated through Google.");
            return session;
        }

        /// <summary>
        /// Login through OAuth with PTC / Google.
        /// </summary>
        /// <param name="loginProvider">The OAuth provider you use to authenticate.</param>
        /// <param name="coordinate">The initial coordinate you will spawn at after logging into PokémonGo.</param>
        /// <param name="deviceWrapper">The <see cref="DeviceWrapper"/> used by the <see cref="Session"/>, keep null if you want a randomly generated <see cref="DeviceWrapper"/>.</param>
        /// <param name = "playerLocale"></param>
        /// <returns></returns>
        public static async Task<Session> GetSession(ILoginProvider loginProvider, GeoCoordinate coordinate, DeviceWrapper deviceWrapper = null, GetPlayerMessage.Types.PlayerLocale playerLocale = null)
        {
            DeviceWrapper device = deviceWrapper ?? DeviceInfoUtil.GetRandomDevice();
            GetPlayerMessage.Types.PlayerLocale locale = playerLocale ?? new GetPlayerMessage.Types.PlayerLocale { Country = "US", Language = "en", Timezone = "America/New_York" };
            string language = locale.Language + "-" + locale.Country;
            var session = new Session(loginProvider, await loginProvider.GetAccessToken(device.UserAgent, language), coordinate, device, locale);
            if (loginProvider is PtcLoginProvider)
                session.Logger.Debug("Authenticated through PTC.");
            else
                session.Logger.Debug("Authenticated through Google.");
            return session;
        }
    }
}
