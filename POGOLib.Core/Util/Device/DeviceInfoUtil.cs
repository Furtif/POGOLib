using System;
using POGOLib.Official.Extensions;
using static POGOProtos.Networking.Envelopes.Signature.Types;

namespace POGOLib.Official.Util.Device
{
    public class DeviceInfoUtil
    {
        private static readonly Random Random = new Random();

        private static readonly string[][] Devices =
        {
            new[] {"iPhone7,1", "iPhone", "N56AP"},    // iPhone 6 Plus
            new[] {"iPhone7,2", "iPhone", "N61AP"},    // iPhone 6
            new[] {"iPhone8,1", "iPhone", "N71AP"},    // iPhone 6s
            new[] {"iPhone8,1", "iPhone", "N71mAP"},   // iPhone 6s
            new[] {"iPhone8,2", "iPhone", "N66AP"},    // iPhone 6s plus
            new[] {"iPhone8,2", "iPhone", "N66mAP"},   // iPhone 6s plus
            new[] {"iPhone8,4", "iPhone", "N69AP"},    // iPhone SE
            new[] {"iPhone8,4", "iPhone", "N69uAP"},   // iPhone SE
            new[] {"iPhone9,1", "iPhone", "D10AP"},    // iPhone 7
            new[] {"iPhone9,2", "iPhone", "D11AP"},    // iPhone 7 plus
            new[] {"iPhone9,3", "iPhone", "D101AP"},   // iPhone 7
            new[] {"iPhone9,4", "iPhone", "D111AP"},   // iPhone 7 plus
            new[] {"iPhone10,1", "iPhone", "D20AP"},   // iPhone 8
            new[] {"iPhone10,2", "iPhone", "D21AP"},   // iPhone 8 plus
            new[] {"iPhone10,3", "iPhone", "D22AP"},   // iPhone X
            new[] {"iPhone10,4", "iPhone", "D201AP"},  // iPhone 8
            new[] {"iPhone10,5", "iPhone", "D211AP"},  // iPhone 8 plus
            new[] {"iphone10,6", "iPhone", "D221AP"}   // iPhone X
        };

        private static readonly string[] OsVersions = {
            "11.1.0",
            "11.2.0",
            "11.2.5",
            "11.3.0",
            "11.4.0"
        };

        private static readonly string[] OsUserAgentParts = {
            "CFNetwork/889.3 Darwin/17.2.0",      // iOS 11.1.0
            "CFNetwork/893.10 Darwin/17.3.0",     // iOS 11.2.0
            "CFNetwork/893.14.2 Darwin/17.4.0",   // iOS 11.2.5
            "CFNetwork/897.1 Darwin/17.5.0",      // iOS 11.3.0
            "CFNetwork/897.15 Darwin/17.5.0"      // iOS 11.4.0
        };

        public static DeviceWrapper GetRandomDevice()
        {
            var device = Devices[Random.Next(Devices.Length)];

            var osId = Random.Next(OsVersions.Length);
            var firmwareType = OsVersions[osId];
            var firmwareUserAgentPart = OsUserAgentParts[osId];

            return new DeviceWrapper
            {
                UserAgent = $"pokemongo/1 {firmwareUserAgentPart}",
                DeviceInfo = new DeviceInfo
                {
                    DeviceId = Random.NextHexNumber(32).ToLower(),
                    DeviceBrand = "Apple",
                    DeviceModelBoot = device[0],
                    DeviceModel = device[1],
                    HardwareModel = device[2],
                    HardwareManufacturer = "Apple",
                    FirmwareBrand = "iPhone OS",
                    FirmwareType = firmwareType,
                }
            };
        }
    }
}
