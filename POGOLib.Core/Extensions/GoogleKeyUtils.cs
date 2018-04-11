using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;

namespace POGOLib.Official.Extensions
{
    internal static class GoogleKeyUtils
    {
        public static RsaKeyParameters KeyFromB64(string b64Key)
        {
            var decodedKey = Convert.FromBase64String(b64Key);

            var modLength = BitConverter.ToInt32(decodedKey.Take(4).Reverse().ToArray(), 0);
            var modBytes = decodedKey.Skip(4).Take(modLength).ToArray();

            var expLength = BitConverter.ToInt32(decodedKey.Skip(modLength + 4).Take(4).Reverse().ToArray(), 0);
            var expBytes = decodedKey.Skip(modLength + 8).Take(expLength).ToArray();

            return new RsaKeyParameters(false, new BigInteger(1, modBytes), new BigInteger(1, expBytes));
        }
        
        public static Dictionary<string, string> ParseAuthResponse(string text)
        {
            return text.Split(new[] {"\n", "\r\n"}, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('='))
                .ToDictionary(parts => parts[0], parts => parts[1]);
        }

        public static string CreateSignature(string email, string password, RsaKeyParameters key)
        {
            var prefix = new byte[] { 0x00 };
            var keyBytes = KeyToStruct(key);
            var bytesToEncrypt = Encoding.UTF8.GetBytes(email + "\x00" + password);

            var messageDigest = new Sha1Digest();
            messageDigest.BlockUpdate(keyBytes, 0, keyBytes.Length);
            var hash = new byte[messageDigest.GetDigestSize()];
            messageDigest.DoFinal(hash, 0);

            var cipher = new OaepEncoding(new RsaEngine());
            cipher.Init(true, key);
            var encrypted = cipher.ProcessBlock(bytesToEncrypt, 0, bytesToEncrypt.Length);

            return UrlSafeBase64(CombineBytes(prefix, hash.Take(4).ToArray(), encrypted));
        }

        private static byte[] KeyToStruct(RsaKeyParameters key)
        {
            byte[] modLength = { 0x00, 0x00, 0x00, 0x80 };
            var mod = key.Modulus.ToByteArrayUnsigned();
            byte[] expLength = { 0x00, 0x00, 0x00, 0x03 };
            var exponent = key.Exponent.ToByteArrayUnsigned();
            return CombineBytes(modLength, mod, expLength, exponent);
        }

        private static byte[] CombineBytes(params byte[][] arrays)
        {
            var rv = new byte[arrays.Sum(a => a.Length)];
            var offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, rv, offset, array.Length);
                offset += array.Length;
            }
            return rv;
        }

        private static string UrlSafeBase64(byte[] byteArray)
        {
            return Convert.ToBase64String(byteArray).Replace('+', '-').Replace('/', '_');
        }
    }
}
