﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using POGOLib.Official.Extensions;
using POGOLib.Official.Util;
using POGOLib.Official.Util.Hash;
using POGOProtos.Networking.Envelopes;
using POGOProtos.Networking.Platform;
using POGOProtos.Networking.Platform.Requests;
using static POGOProtos.Networking.Envelopes.Signature.Types;
using static POGOProtos.Networking.Envelopes.RequestEnvelope.Types;
using POGOLib.Official.Exceptions;

namespace POGOLib.Official.Net
{
    internal class RpcEncryption
    {
        /// <summary>
        /// The authenticated <see cref="Session"/>.
        /// </summary>
        private readonly Session _session;

        /// <summary>
        /// The <see cref="Stopwatch"/> that has been running since <see cref="Session.StartupAsync"/>.
        /// </summary>
        private readonly Stopwatch _stopwatch;

        /// <summary>
        /// The session hash for the <see cref="Signature"/> which is 16 randomly generated bytes.
        /// </summary>
        private readonly ByteString _sessionHash;

        /// <summary>
        /// Holds the value that was used in the previous <see cref="BuildLocationFixes"/> iteration.
        /// </summary>
        private long _lastTimestampSinceStart;

        private readonly Uk27IdGenerator uk27IdGenerator = new Uk27IdGenerator();

        internal RpcEncryption(Session session)
        {
            _session = session;
            _stopwatch = Stopwatch.StartNew();

            var sessionHash = new byte[16];
            session.Random.NextBytes(sessionHash);

            _sessionHash = ByteString.CopyFrom(sessionHash);
            _lastTimestampSinceStart = 0;
        }

        private long TimestampSinceStartMs => _stopwatch.ElapsedMilliseconds;

        /// <summary>
        /// Generates a few random <see cref="LocationFix"/>es to act like a real GPS sensor.
        /// </summary>
        /// <param name="requestEnvelope">The <see cref="RequestEnvelope"/> these <see cref="LocationFix"/>es are used for.</param>
        /// <param name="timestampSinceStart">The milliseconds passed since starting the <see cref="Session"/> used by the current <see cref="RequestEnvelope"/>.</param>
        /// <returns></returns>
        private List<LocationFix> BuildLocationFixes(RequestEnvelope requestEnvelope, long timestampSinceStart)
        {
            var locationFixes = new List<LocationFix>();

            // TODO: review this. those two lines are removed to can do empty requests
            //if (requestEnvelope.Requests.Count == 0 || requestEnvelope.Requests[0] == null)
            //    return locationFixes;

            // Determine amount of location fixes.
            //      We look for the amount of seconds that have passed since the last location fixes request.
            //      If that results in 0, send 1 location fix.
            var millisecondsPerFix = _session.Random.Next(995, 999);
            var providerCount = Math.Min((timestampSinceStart - _lastTimestampSinceStart) / millisecondsPerFix, _session.Random.Next(8, 11));
            if (providerCount == 0)
                providerCount = 1;

            // Determine size of the "play around" window.
            //      Not so relevant when starting up.
            var totalMilliseconds = providerCount * millisecondsPerFix;
            var baseTimestampSnapshot = Math.Max(timestampSinceStart - totalMilliseconds, 0);
            var playAroundWindow = Math.Max(0, baseTimestampSnapshot - _lastTimestampSinceStart);

            if (playAroundWindow == 0 && providerCount == 1 && millisecondsPerFix >= timestampSinceStart)
            {
                // We really need an offset for this one..
                playAroundWindow = _session.Random.Next(0, (int)timestampSinceStart);
            }
            else
            {
                // A small offset between location fixes.
                playAroundWindow = Math.Min(playAroundWindow, providerCount * 2);
            }

            // Share "play around" window over all location fixes.
            var playAroundWindowPart = playAroundWindow != 0
                ? playAroundWindow / providerCount
                : 1;

            for (var i = 0; i < providerCount; i++)
            {
                var timestampSnapshot = baseTimestampSnapshot;
                // Apply current location fix position.
                timestampSnapshot += i * millisecondsPerFix;
                // Apply an offset.
                timestampSnapshot += _session.Random.Next(0, (int)((i + 1) * playAroundWindowPart));

                locationFixes.Add(new LocationFix
                {
                    TimestampSnapshot = (ulong)timestampSnapshot,
                    Latitude = LocationUtil.OffsetLatitudeLongitude(_session.Player.Coordinate.Latitude, _session.Random.Next(100) + 10),
                    Longitude = LocationUtil.OffsetLatitudeLongitude(_session.Player.Coordinate.Longitude, _session.Random.Next(100) + 10),
                    HorizontalAccuracy = (float)_session.Random.NextDouble(5.0, 25.0),
                    VerticalAccuracy = (float)_session.Random.NextDouble(5.0, 25.0),
                    Altitude = (float)_session.Random.NextDouble(10.0, 30.0),
                    Provider = "fused",
                    ProviderStatus = 3,
                    LocationType = 1,
                    Course = -1,
                    Speed = -1
                    // Floor = 0
                });
            }

            _lastTimestampSinceStart = timestampSinceStart;

            return locationFixes;
        }

        /// <summary>
        /// Generates the encrypted signature which is required for the <see cref="RequestEnvelope"/>.
        /// </summary>
        /// <returns>The encrypted <see cref="PlatformRequest"/>.</returns>
        internal async Task<PlatformRequest> GenerateSignatureAsync(RequestEnvelope requestEnvelope)
        {
            if (Configuration.Hasher == null)
            {
                throw new PokeHashException($"{nameof(Configuration.Hasher)} is not set, which is required to send valid calls to PokemonGo.");
            }

            var timestampSinceStart = TimestampSinceStartMs;
            var locationFixes = BuildLocationFixes(requestEnvelope, timestampSinceStart);
            var locationFix = locationFixes.Last();

            _session.Player.Coordinate.HorizontalAccuracy = locationFix.HorizontalAccuracy;
            _session.Player.Coordinate.VerticalAccuracy = locationFix.VerticalAccuracy;
            _session.Player.Coordinate.Altitude = locationFix.Altitude;

            requestEnvelope.Accuracy = _session.Player.Coordinate.Altitude; // _session.Player.Coordinate.HorizontalAccuracy;
            requestEnvelope.MsSinceLastLocationfix = timestampSinceStart - (long)locationFix.TimestampSnapshot;

            var signature = new Signature
            {
                TimestampSinceStart = (ulong)timestampSinceStart,
                Timestamp = (ulong)TimeUtil.GetCurrentTimestampInMilliseconds(),
                SensorInfo =
                {
                    new SensorInfo
                    {
                        TimestampSnapshot = (ulong) (timestampSinceStart + _session.Random.Next(100, 250)),
                        LinearAccelerationX = -0.7 + _session.Random.NextDouble() * 1.4,
                        LinearAccelerationY = -0.7 + _session.Random.NextDouble() * 1.4,
                        LinearAccelerationZ = -0.7 + _session.Random.NextDouble() * 1.4,
                        RotationRateX = 0.7 * _session.Random.NextDouble(),
                        RotationRateY = 0.8 * _session.Random.NextDouble(),
                        RotationRateZ = 0.8 * _session.Random.NextDouble(),
                        AttitudePitch = -1.0 + _session.Random.NextDouble() * 2.0,
                        AttitudeRoll = -1.0 + _session.Random.NextDouble() * 2.0,
                        AttitudeYaw = -1.0 + _session.Random.NextDouble() * 2.0,
                        GravityX = -1.0 + _session.Random.NextDouble() * 2.0,
                        GravityY = -1.0 + _session.Random.NextDouble() * 2.0,
                        GravityZ = -1.0 + _session.Random.NextDouble() * 2.0,
                        MagneticFieldAccuracy = -1,
                        Status = 3
                    }
                },
                DeviceInfo = _session.Device.DeviceInfo,
                LocationFix = { locationFixes },
                ActivityStatus = new ActivityStatus
                {
                    Stationary = true
                }
            };

            // Hashing
            signature.SessionHash = _sessionHash;
            signature.Unknown25 = Configuration.Hasher.Unknown25;
            signature.Unknown27 = uk27IdGenerator.Next();

            var serializedTicket = requestEnvelope.AuthTicket != null ? requestEnvelope.AuthTicket.ToByteArray() : requestEnvelope.AuthInfo.ToByteArray();
            var locationBytes = BitConverter.GetBytes(_session.Player.Coordinate.Latitude).Reverse()
                .Concat(BitConverter.GetBytes(_session.Player.Coordinate.Longitude).Reverse())
                .Concat(BitConverter.GetBytes(_session.Player.Coordinate.Altitude).Reverse()).ToArray();

            var requestsBytes = requestEnvelope.Requests.Select(x => x.ToByteArray()).ToArray();

            HashData hashData = null;

            do
            {
                try
                {
                    hashData = await Configuration.Hasher.GetHashDataAsync(requestEnvelope, signature, locationBytes, requestsBytes, serializedTicket);
                }
                catch (TimeoutException)
                {
                    throw new PokeHashException("Hasher server might down - timeout out");
                }
                catch (PokeHashException ex1)
                {
                    throw ex1;
                }
                catch (SessionStateException ex1)
                {
                    throw ex1;
                }
                catch (Exception ex1)
                {
                    throw new PokeHashException($"Missed Hash Data. {ex1}");
                }
            } while (hashData == null);

            if (hashData == null)
                throw new PokeHashException($"Missed Hash Data.");

            signature.LocationHash1 = (int)hashData.LocationAuthHash;
            signature.LocationHash2 = (int)hashData.LocationHash;
            signature.RequestHash.AddRange(hashData.RequestHashes);

            var encryptedSignature = new PlatformRequest
            {
                Type = PlatformRequestType.SendEncryptedSignature,
                RequestMessage = new SendEncryptedSignatureRequest
                {
                    EncryptedSignature = ByteString.CopyFrom(Configuration.Hasher.GetEncryptedSignature(signature.ToByteArray(), (uint)timestampSinceStart))
                }.ToByteString()
            };

            return encryptedSignature;
        }
    }
}
