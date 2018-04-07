using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;

namespace POGOLib.Official.Extensions
{
    // ReSharper disable once InconsistentNaming
    public class GPSOAuthClient
    {
        private const string B64Key = "AAAAgMom/1a/v0lblO2Ubrt60J2gcuXSljGFQXgcyZWveWLEwo6prwgi3" + 
            "iJIZdodyhKZQrNWp5nKJ3srRXcUW+F1BD3baEVGcmEgqaLZUNBjm057pK" + 
            "RI16kB0YppeGx5qIQ5QjKzsR8ETQbKLNWgRY0QRNVz34kMJR3P/LgHax/" + 
            "6rmf5AAAAAwEAAQ==";
        private const string Version = "0.0.5";
        private const string AuthUrl = "https://android.clients.google.com/auth";
        private const string UserAgent = "GPSOAuthSharp/" + Version;

        private readonly RsaKeyParameters _androidKey = GoogleKeyUtils.KeyFromB64(B64Key);
        private readonly string _email;
        private readonly string _password;

        public GPSOAuthClient(string email, string password)
        {
            _email = email;
            _password = password;
        }
        
        private static async Task<Dictionary<string, string>> PerformAuthRequest(Dictionary<string, string> data)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
                var postResponse = await client.PostAsync(AuthUrl, new FormUrlEncodedContent(data.ToArray()));
                var result = await postResponse.Content.ReadAsStringAsync();
                return GoogleKeyUtils.ParseAuthResponse(result);
            }
        }
        
        public async Task<Dictionary<string, string>> PerformMasterLogin(string service = "ac2dm",
            string deviceCountry = "us", string operatorCountry = "us", string lang = "en", int sdkVersion = 21)
        {
            var signature = GoogleKeyUtils.CreateSignature(_email, _password, _androidKey);
            var dict = new Dictionary<string, string> {
                { "accountType", "HOSTED_OR_GOOGLE" },
                { "Email", _email },
                { "has_permission", 1.ToString() },
                { "add_account", 1.ToString() },
                { "EncryptedPasswd",  signature},
                { "service", service },
                { "source", "android" },
                { "device_country", deviceCountry },
                { "operatorCountry", operatorCountry },
                { "lang", lang },
                { "sdk_version", sdkVersion.ToString() }
            };
            return await PerformAuthRequest(dict);
        }
        
        public async Task<Dictionary<string, string>> PerformOAuth(string masterToken, string service, string app, string clientSig,
            string deviceCountry = "us", string operatorCountry = "us", string lang = "en", int sdkVersion = 21)
        {
            var dict = new Dictionary<string, string> {
                { "accountType", "HOSTED_OR_GOOGLE" },
                { "Email", _email },
                { "has_permission", 1.ToString() },
                { "EncryptedPasswd",  masterToken},
                { "service", service },
                { "source", "android" },
                { "app", app },
                { "client_sig", clientSig },
                { "device_country", deviceCountry },
                { "operatorCountry", operatorCountry },
                { "lang", lang },
                { "sdk_version", sdkVersion.ToString() }
            };
            return await PerformAuthRequest(dict);
        }
    }
}
