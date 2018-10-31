using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using POGOLib.Official.Util;
using POGOProtos.Map;
using POGOProtos.Networking.Envelopes;
using POGOProtos.Networking.Requests;
using POGOProtos.Networking.Requests.Messages;
using POGOProtos.Networking.Responses;
using POGOProtos.Enums;
using POGOProtos.Networking.Platform;
using POGOProtos.Networking.Platform.Requests;
using POGOProtos.Networking.Platform.Responses;
using POGOLib.Official.Extensions;
using POGOProtos.Map.Pokemon;
using POGOLib.Official.Exceptions;
using Google.Protobuf.Collections;

namespace POGOLib.Official.Net
{
    public class RpcClient : IDisposable
    {
        const string INITIAL_PTR8 = "d9ad6deadadfd59abc0e8a85d01e2f6140e6e79b";
        /// <summary>
        ///     The authenticated <see cref="Session" />.
        /// </summary>
        private readonly Session _session;

        /// <summary>
        ///     The class responsible for all encryption / signing regarding <see cref="RpcClient"/>.
        /// </summary>
        private readonly RpcEncryption _rpcEncryption;

        /// <summary>
        ///     The current request count we are at.
        /// </summary>
        private ulong _requestCount;

        /// <summary>
        ///     The rpc url we have to call.
        /// </summary>
        private string _requestUrl;

        private string _mapKey = INITIAL_PTR8;

        private readonly RandomIdGenerator idGenerator = new RandomIdGenerator();

        private readonly List<RequestType> _defaultRequests = new List<RequestType>
        {
            RequestType.CheckChallenge,
            RequestType.GetHatchedEggs,
            RequestType.GetHoloholoInventory,
            RequestType.CheckAwardedBadges,
            RequestType.DownloadSettings,
            RequestType.GetBuddyWalked,
            RequestType.GetInbox
        };

        private readonly ConcurrentQueue<RequestEnvelope> _rpcQueue = new ConcurrentQueue<RequestEnvelope>();

        private readonly ConcurrentDictionary<RequestEnvelope, ByteString> _rpcResponses = new ConcurrentDictionary<RequestEnvelope, ByteString>();

        private readonly Semaphore _rpcQueueMutex = new Semaphore(1, 1);

        internal RpcClient(Session session)
        {
            _session = session;
            _rpcEncryption = new RpcEncryption(session);
            _mapKey = INITIAL_PTR8;
        }

        internal DateTime LastRpcRequest { get; private set; }

        internal DateTime LastRpcMapObjectsRequest { get; private set; }

        internal GeoCoordinate LastGeoCoordinateMapObjectsRequest { get; private set; } = new GeoCoordinate();

        internal Platform GetPlatform()
        {
            return _session.Device.DeviceInfo.DeviceBrand == "Apple" ? Platform.Ios : Platform.Android;
        }

        private long PositiveRandom()
        {
            long ret = _session.Random.Next() | (_session.Random.Next() << 32);
            // lrand48 ensures it's never < 0
            // So do the same
            if (ret < 0)
                ret = -ret;
            return ret;
        }

        private void IncrementRequestCount()
        {
            // Request counts on android jump more than 1 at a time according to logs
            // They are fully sequential on iOS though
            // So mimic that same behavior here.
            switch (GetPlatform())
            {
                case Platform.Android:
                    _requestCount += (uint)_session.Random.Next(2, 15);
                    break;
                case Platform.Ios:
                    _requestCount++;
                    break;
            }
        }

        /// <summary>
        /// Sends all requests which the (ios-)client sends on startup
        /// </summary>
        // NOTE: this is the new login process in the real app, after of 0.45 API
        internal async Task<bool> StartupAsync()
        {
            // Empty request. To change the _requestUrl variable.
            await SendRemoteProcedureCallAsync();

            GetPlayerResponse playerResponse;
            int loop = 0;

            do
            {
                var response = await SendRemoteProcedureCallAsync(new Request
                {
                    RequestType = RequestType.GetPlayer,
                    RequestMessage = new GetPlayerMessage
                    {
                        PlayerLocale = _session.Player.PlayerLocale,
                        //New in protos ???
                        //PreventCreation = true
                    }.ToByteString()
                });

                if (response == null)
                {
                    _session.Logger.Warn("Pokemon Go service as maybe issues, Please try later");
                    //return false;
                }

                playerResponse = GetPlayerResponse.Parser.ParseFrom(response);

                if (!playerResponse.Success)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1000));
                }
                loop++;
            } while (!playerResponse.Success && loop < 5);

            _session.Player.Data = playerResponse.PlayerData;
            _session.Player.Banned = playerResponse.Banned;
            _session.Player.Warn = playerResponse.Warn;

            //New in Protos ???
            //if (playerResponse.WasCreated)
            //{
            //    _session.Logger.Notice("This account is created.");
            //}

            if (playerResponse.Warn)
            {
                _session.Logger.Warn("This account is flagged.");
            }
            if (playerResponse.Banned)
            {
                _session.Logger.Error("This account is banned.");
                return false;
            }

            await DownloadRemoteConfig();
            await GetAssetDigest();
            await DownloadItemTemplates();
            await GetDownloadURLs();

            //Here tutorial
            //

            //GetStoreItems
            await SendRemoteProcedureCallAsync(PlatformRequestType.GetStoreItems);

            //FetchAllNews
            RepeatedField<string> NewsIds = await FetchAllNews();

            //MarkReadNewsArticle
            if (NewsIds.Count > 1)
                await MarkReadNewsArticle(NewsIds);

            return true;
        }

        /// <summary>
        ///     It is not recommended to call this. Map objects will update automatically and fire the map update event.
        /// </summary>
        public async Task RefreshMapObjectsAsync()
        {
            var cellIds = MapUtil.GetCellIdsForLatLong(_session.Player.Coordinate.Latitude, _session.Player.Coordinate.Longitude);
            var sinceTimeMs = cellIds.Select(x => (long)0).ToArray();

            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.GetMapObjects,
                RequestMessage = new GetMapObjectsMessage
                {
                    CellId = { cellIds },
                    SinceTimestampMs = { sinceTimeMs },
                    Latitude = _session.Player.Coordinate.Latitude,
                    Longitude = _session.Player.Coordinate.Longitude
                }.ToByteString()
            });

            if (response != null)
            {
                var mapObjects = GetMapObjectsResponse.Parser.ParseFrom(response);
                if (mapObjects.Status == MapObjectsStatus.Success)
                {
                    // TODO: Cleaner?
                    var pokemonCatchable = mapObjects.MapCells.SelectMany(c => c.CatchablePokemons).Count();
                    var pokemonWild = mapObjects.MapCells.SelectMany(c => c.WildPokemons).Count();
                    var pokemonNearby = mapObjects.MapCells.SelectMany(c => c.NearbyPokemons).Count();
                    var pokemonCount = pokemonCatchable + pokemonWild + pokemonNearby;

                    _session.Logger.Debug($"Received '{mapObjects.MapCells.Count}' map cells.");
                    _session.Logger.Debug($"Received '{pokemonCount}' pokemons. Catchable({pokemonCatchable}) Wild({pokemonWild}) Nearby({pokemonNearby})");
                    _session.Logger.Debug($"Received '{mapObjects.MapCells.SelectMany(c => c.Forts).Count()}' forts.");

                    if (mapObjects.MapCells.Count == 0)
                    {
                        throw new SessionStateException("We received 0 map cells, are your session is stoped?");
                    }

                    _session.Map.Cells = mapObjects.MapCells;
                    return;
                }
                else
                {
                    _session.Map.Cells = new RepeatedField<MapCell>();
                    throw new SessionStateException($"GetMapObjects status is: '{mapObjects.Status}'.");
                }
            }
            else if (_session.State != SessionState.Paused)
            {
                // POGOLib didn't expect this.
                _session.Map.Cells = new RepeatedField<MapCell>();
                throw new SessionStateException("We received 0 map cells or status is empty.");
            }

            _session.Map.Cells = new RepeatedField<MapCell>();
            throw new SessionStateException("We received 0 map cells or status is empty.");
        }
        /// <summary>
        ///     Gets the next request id for the <see cref="RequestEnvelope" />.
        /// </summary>
        /// <returns></returns>
        private ulong GetNextRequestId()
        {
            //Change to random requestId https://github.com/pogodevorg/pgoapi/pull/217

            return idGenerator.Next();
        }

        /// <summary>
        ///     Gets a collection of requests that should be sent in every request to PokémonGo along with your own
        ///     <see cref="Request" />.
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Request> GetDefaultRequests(bool nobuddy, bool noinbox)
        {
            var request = new List<Request>
            {
                new Request
                {
                    RequestType = RequestType.CheckChallenge,
                    RequestMessage = new CheckChallengeMessage
                    {
                        DebugRequest = false
                    }.ToByteString()
                },
                new Request
                {
                    RequestType = RequestType.GetHatchedEggs,
                    RequestMessage = new GetHatchedEggsMessage().ToByteString()
                },
                new Request
                {
                    RequestType = RequestType.GetHoloholoInventory,
                    RequestMessage = new GetHoloInventoryMessage
                    {
                        LastTimestampMs = _session.Player.Inventory.LastInventoryTimestampMs
                    }.ToByteString()
                },
                new Request
                {
                    RequestType = RequestType.CheckAwardedBadges,
                    RequestMessage = new CheckAwardedBadgesMessage().ToByteString()
                }
            };

            if (string.IsNullOrEmpty(_session.GlobalSettingsHash))
            {
                request.Add(new Request
                {
                    RequestType = RequestType.DownloadSettings
                });
            }
            else
            {
                request.Add(new Request
                {
                    RequestType = RequestType.DownloadSettings,
                    RequestMessage = new DownloadSettingsActionMessage
                    {
                        Hash = _session.GlobalSettingsHash
                    }.ToByteString()
                });
            }
            if (!nobuddy)
            {
                request.Add(new Request
                {
                    RequestType = RequestType.GetBuddyWalked,
                    RequestMessage = new GetBuddyWalkedMessage().ToByteString()
                });
            }
            // Need maybe other ordre....
            if (_session.IncenseUsed)
            {
                request.Add(new Request
                {
                    RequestType = RequestType.GetIncensePokemon,
                    RequestMessage = new GetIncensePokemonMessage
                    {
                        PlayerLatitude = _session.Player.Coordinate.Latitude,
                        PlayerLongitude = _session.Player.Coordinate.Longitude
                    }.ToByteString()
                });
            }
            if (!noinbox)
            {
                request.Add(new Request
                {
                    RequestType = RequestType.GetInbox,
                    RequestMessage = new GetInboxMessage
                    {
                        IsHistory = true
                    }.ToByteString()
                });
            }
            return request;
        }

        /// <summary>
        ///     Gets a <see cref="RequestEnvelope" /> with authentication data.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="addDefaultRequests"></param>
        /// <param name = "nobuddy"></param>
        /// <param name = "noinbox"></param>
        /// <returns></returns>
        public async Task<RequestEnvelope> GetRequestEnvelopeAsync(IEnumerable<Request> request, bool addDefaultRequests, bool nobuddy = false, bool noinbox = false)
        {
            var requestEnvelope = new RequestEnvelope
            {
                StatusCode = 2,
                RequestId = GetNextRequestId(),
                Latitude = _session.Player.Coordinate.Latitude,
                Longitude = _session.Player.Coordinate.Longitude
            };

            if (request != null && request.FirstOrDefault().RequestType != RequestType.MethodUnset)
            {
                requestEnvelope.Requests.AddRange(request);

                if (addDefaultRequests)
                    requestEnvelope.Requests.AddRange(GetDefaultRequests(nobuddy, noinbox));
            }

            if (_session.AccessToken.AuthTicket != null && _session.AccessToken.AuthTicket.ExpireTimestampMs < ((ulong)TimeUtil.GetCurrentTimestampInMilliseconds() - (60000 * 2)))
            {
                // Check for almost expired AuthTicket (2 minute buffer). Null out the AuthTicket so that AccessToken is used.
                _session.AccessToken.AuthTicket = null;
            }

            if (_session.AccessToken.AuthTicket == null || _session.AccessToken.IsExpired || string.IsNullOrEmpty(_session.AccessToken.Token))
            {
                if (_session.AccessToken.IsExpired || string.IsNullOrEmpty(_session.AccessToken.Token))
                {
                    await _session.GetValidAccessToken(true);
                }

                requestEnvelope.AuthInfo = new RequestEnvelope.Types.AuthInfo
                {
                    Provider = _session.AccessToken.ProviderID,
                    Token = new RequestEnvelope.Types.AuthInfo.Types.JWT
                    {
                        Contents = _session.AccessToken.Token,
                        Unknown2 = 59
                    }
                };
            }
            else
            {
                requestEnvelope.AuthTicket = _session.AccessToken.AuthTicket;
            }

            requestEnvelope.PlatformRequests.Add(await _rpcEncryption.GenerateSignatureAsync(requestEnvelope));

            // Apply new UnknownPtr8.
            if (Configuration.Hasher.AppVersion > 4500)
            {
                if (requestEnvelope.Requests.Count > 0 && (
                    requestEnvelope.Requests[0].RequestType == RequestType.GetMapObjects ||
                    requestEnvelope.Requests[0].RequestType == RequestType.GetPlayer))
                {
                    requestEnvelope.PlatformRequests.Add(new RequestEnvelope.Types.PlatformRequest
                    {
                        Type = PlatformRequestType.UnknownPtr8,
                        RequestMessage = new UnknownPtr8Request
                        {
                            Message = _mapKey
                        }.ToByteString()
                    });
                }
            }

            return requestEnvelope;
        }

        public async Task<ByteString> SendRemoteProcedureCallAsync(RequestType requestType)
        {
            return await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = requestType
            });
        }

        public async Task<ByteString> SendRemoteProcedureCallAsync(PlatformRequestType type = PlatformRequestType.MethodUnset)
        {
            var requestEnvelope = await GetRequestEnvelopeAsync(null, false);

            switch (type)
            {
                case PlatformRequestType.BuyItemAndroid:
                    break;
                case PlatformRequestType.BuyItemIos:
                    break;
                case PlatformRequestType.BuyItemPokecoins:
                    break;
                case PlatformRequestType.GetStoreItems:
                    requestEnvelope.PlatformRequests.Add(new RequestEnvelope.Types.PlatformRequest
                    {
                        Type = PlatformRequestType.GetStoreItems,
                        RequestMessage = new GetStoreItemsRequest().ToByteString()
                    });
                    break;
                case PlatformRequestType.JoinEvent:
                    break;
                case PlatformRequestType.MethodUnset:
                    break;
                case PlatformRequestType.SendEncryptedSignature:
                    break;
                case PlatformRequestType.UnknownPtr8:
                    break;
            }

            return await SendRemoteProcedureCall(requestEnvelope);
        }

        public async Task<ByteString> SendRemoteProcedureCallAsync(Request request, bool addDefaultRequests = true, bool nobuddy = false, bool noinbox = false)
        {
            return await SendRemoteProcedureCall(await GetRequestEnvelopeAsync(new[] { request }, addDefaultRequests, nobuddy, noinbox));
        }

        public async Task<ByteString> SendRemoteProcedureCallAsync(Request[] request, bool addDefaultRequests = false, bool nobuddy = false, bool noinbox = false)
        {
            return await SendRemoteProcedureCall(await GetRequestEnvelopeAsync(request, addDefaultRequests, nobuddy, noinbox));
        }

        private async Task<ByteString> SendRemoteProcedureCall(RequestEnvelope requestEnvelope)
        {
            return await Task.Run(async () =>
            {
                //Session is paused this is not in semafore!!
                if (requestEnvelope.Requests.FirstOrDefault()?.RequestType == RequestType.VerifyChallenge && !Configuration.IgnoreRPCSemafore)
                {
                    _rpcResponses.GetOrAdd(requestEnvelope, await PerformRemoteProcedureCallAsync(requestEnvelope));

                    ByteString req;
                    _rpcResponses.TryRemove(requestEnvelope, out req);
                    return req;
                }

                _rpcQueue.Enqueue(requestEnvelope);

                try
                {
                    if (!Configuration.IgnoreRPCSemafore)
                        _rpcQueueMutex.WaitOne();

                    RequestEnvelope processRequestEnvelope;

                    while (_rpcQueue.TryDequeue(out processRequestEnvelope))
                    {
                        if (Configuration.ThrottleDifference > 0)
                        {
                            //var diff = Math.Max(0, DateTime.Now.Millisecond - LastRpcRequest.Millisecond);
                            var diff = (int)Math.Min((DateTime.UtcNow - LastRpcRequest.ToUniversalTime()).TotalMilliseconds, Configuration.ThrottleDifference);
                            if (diff < Configuration.ThrottleDifference)
                            {
                                var delay = Configuration.ThrottleDifference - diff + (int)(_session.Random.NextDouble() * 0);

                                await Task.Delay(delay);
                            }
                        }

                        _rpcResponses.GetOrAdd(processRequestEnvelope, await PerformRemoteProcedureCallAsync(processRequestEnvelope));
                    }

                    ByteString ret;
                    _rpcResponses.TryRemove(requestEnvelope, out ret);

                    return ret;

                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
                finally
                {
                    if (!Configuration.IgnoreRPCSemafore)
                        _rpcQueueMutex.Release();
                }
            });
        }

        private async Task<ByteString> PerformRemoteProcedureCallAsync(RequestEnvelope requestEnvelope)
        {
            if (requestEnvelope == null)
            {
                return null;
            }

            try
            {
                switch (_session.State)
                {
                    case SessionState.Stopped:
                        _session.Logger.Error("We tried to send a request while the session was stopped.");
                        return null;

                    case SessionState.TemporalBanned:
                        _session.Logger.Error("We tried to send a request while the session was temporal banned.");
                        return null;

                    case SessionState.Paused:
                        var requests = requestEnvelope.Requests.Select(x => x.RequestType).ToList();
                        if (requests.Count != 1 || requests[0] != RequestType.VerifyChallenge)
                        {
                            _session.Logger.Debug("We tried to send a request while the session was paused. The only request allowed is VerifyChallenge.");
                            return null;
                        }
                        break;
                }

                using (var requestData = new ByteArrayContent(requestEnvelope.ToByteArray()))
                {
                    if (requestData == null)
                    {
                        await Task.Delay(10000); //wait 10 secs on grave bug
                        throw new SessionStateException($"RequestData is null");
                    }

                    using (var response = await _session.HttpClient.PostAsync(_requestUrl ?? Constants.ApiUrl, requestData))
                    {
                        if (response == null)
                        {
                            await Task.Delay(10000); //wait 10 secs on grave bug
                            throw new SessionStateException($"RequestData response is null");
                        }

                        _session.Logger.Debug("Sending RPC Request: '" + string.Join(", ", requestEnvelope.Requests.Select(x => x.RequestType)) + "'");
                        _session.Logger.Debug("=> Platform Request: '" + string.Join(", ", requestEnvelope.PlatformRequests.Select(x => x.Type)) + "'");

                        if (!response.IsSuccessStatusCode)
                        {
                            _session.Logger.Warn(await response.Content.ReadAsStringAsync());
                            await Task.Delay(10000); //wait 10 secs on grave bug
                            throw new Exception("Received a non-success HTTP status code from the RPC server, see the console for the response.");
                        }

                        var responseBytes = await response.Content.ReadAsByteArrayAsync();
                        var responseEnvelope = ResponseEnvelope.Parser.ParseFrom(responseBytes);

                        switch (responseEnvelope.StatusCode)
                        {
                            // Valid response.
                            case ResponseEnvelope.Types.StatusCode.Ok:
                                // Success!?                                
                                break;

                            // Valid response and new rpc url.
                            case ResponseEnvelope.Types.StatusCode.OkRpcUrlInResponse:
                                if (Regex.IsMatch(responseEnvelope.ApiUrl, "pgorelease\\.nianticlabs\\.com\\/plfe\\/\\d+"))
                                {
                                    _requestUrl = $"https://{responseEnvelope.ApiUrl}/rpc";
                                }
                                else
                                {
                                    await Task.Delay(10000); //wait 10 secs on grave bug
                                    throw new Exception($"Received an incorrect API url: '{responseEnvelope.ApiUrl}', status code was: '{responseEnvelope.StatusCode}'.");
                                }
                                break;

                            // A new rpc endpoint is available.
                            case ResponseEnvelope.Types.StatusCode.Redirect:
                                if (Regex.IsMatch(responseEnvelope.ApiUrl, "pgorelease\\.nianticlabs\\.com\\/plfe\\/\\d+"))
                                {
                                    _requestUrl = $"https://{responseEnvelope.ApiUrl}/rpc";
                                    return await PerformRemoteProcedureCallAsync(requestEnvelope);
                                }
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new Exception($"Received an incorrect API url: '{responseEnvelope.ApiUrl}', status code was: '{responseEnvelope.StatusCode}'.");

                            // The login token is invalid.
                            // TODO: Make cleaner to reduce duplicate code with the GetRequestEnvelopeAsync method.
                            case ResponseEnvelope.Types.StatusCode.InvalidAuthToken:
                                _session.Logger.Debug("Received StatusCode 102, reauthenticating.");

                                await _session.GetValidAccessToken(true);

                                // Apply new token.
                                requestEnvelope.AuthInfo = new RequestEnvelope.Types.AuthInfo
                                {
                                    Provider = _session.AccessToken.ProviderID,
                                    Token = new RequestEnvelope.Types.AuthInfo.Types.JWT
                                    {
                                        Contents = _session.AccessToken.Token,
                                        Unknown2 = 59
                                    }
                                };

                                // Clear all PlatformRequests.
                                requestEnvelope.PlatformRequests.Clear();

                                // Apply new UnknownPtr8.
                                if (Configuration.Hasher.AppVersion > 4500)
                                {
                                    requestEnvelope.PlatformRequests.Add(new RequestEnvelope.Types.PlatformRequest()
                                    {
                                        Type = PlatformRequestType.UnknownPtr8,
                                        RequestMessage = new UnknownPtr8Request
                                        {
                                            Message = _mapKey
                                        }.ToByteString()
                                    });
                                }
                                // Apply new PlatformRequests to envelope.
                                requestEnvelope.PlatformRequests.Add(await _rpcEncryption.GenerateSignatureAsync(requestEnvelope));

                                // Re-send envelope.
                                ByteString resend = null;
                                resend = await PerformRemoteProcedureCallAsync(requestEnvelope);

                                if (resend == null)
                                    throw new SessionStateException("INVALID AUTH TOKEN");
                                else
                                    return resend;
                                throw new SessionStateException("INVALID AUTH TOKEN");
                            case ResponseEnvelope.Types.StatusCode.BadRequest:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new APIBadRequestException("BAD REQUEST");
                            case ResponseEnvelope.Types.StatusCode.SessionInvalidated:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new SessionInvalidatedException("SESSION INVALIDATED");
                            case ResponseEnvelope.Types.StatusCode.Unknown:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new SessionUnknowException("UNKNOWN");
                            case ResponseEnvelope.Types.StatusCode.InvalidPlatformRequest:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new InvalidPlatformException("INVALID PLATFORM REQUEST");
                            case ResponseEnvelope.Types.StatusCode.InvalidRequest:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new InvalidPlatformException("INVALID REQUEST");
                            default:
                                await Task.Delay(10000); //wait 10 secs on grave bug
                                throw new Exception($"Unknown status code: {responseEnvelope.StatusCode}");
                        }

                        LastRpcRequest = DateTime.UtcNow;
                        if (requestEnvelope.Requests[0].RequestType == RequestType.GetMapObjects)
                        {
                            LastRpcMapObjectsRequest = LastRpcRequest;
                            LastGeoCoordinateMapObjectsRequest = _session.Player.Coordinate;
                        }

                        var mapPlatform = responseEnvelope.PlatformReturns.FirstOrDefault(x => x.Type == PlatformRequestType.UnknownPtr8);
                        if (mapPlatform != null)
                        {
                            var unknownPtr8Response = UnknownPtr8Response.Parser.ParseFrom(mapPlatform.Response);
                            _mapKey = unknownPtr8Response.Message;
                        }

                        if (responseEnvelope.AuthTicket != null)
                        {
                            _session.AccessToken.AuthTicket = responseEnvelope.AuthTicket;
                            _session.Logger.Debug("Received a new AuthTicket from Pokemon!");
                        }

                        return HandleResponseEnvelope(requestEnvelope, responseEnvelope);
                    }
                }
            }
            catch (SessionInvalidatedException ex)
            {
                throw ex;
            }
            catch (InvalidPlatformException ex)
            {
                throw ex;
            }
            catch (APIBadRequestException ex)
            {
                if (requestEnvelope.Requests.FirstOrDefault().RequestType == RequestType.DownloadRemoteConfigVersion
                    || requestEnvelope.Requests.FirstOrDefault().RequestType == RequestType.DownloadItemTemplates
                    || requestEnvelope.Requests.FirstOrDefault().RequestType == RequestType.GetAssetDigest
                    || requestEnvelope.Requests.FirstOrDefault().RequestType == RequestType.GetDownloadUrls)
                {
                    _session.SetTemporalBan();
                    throw new SessionStateException("Your account may be temporary banned! please try from the official client.");
                }

                throw new APIBadRequestException(ex.Message);
            }
            catch (SessionUnknowException ex)
            {
                throw ex;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw ex;
            }
            catch (PokeHashException ex)
            {
                throw ex;
            }
            catch (PtcLoginException ex)
            {
                throw ex;
            }
            catch (HashVersionMismatchException ex)
            {
                throw ex;
            }
            catch (GoogleLoginException ex)
            {
                throw ex;
            }
            catch (SessionStateException e)
            {
                throw e;
            }
            catch (Exception ex)
            {
                throw new SessionStateException($"Your account may be temporary banned! please try from the official client." + ex);
            }
        }

        /// <summary>
        /// Responsible for handling the received <see cref="ResponseEnvelope" />.
        /// </summary>
        /// <param name="requestEnvelope">The <see cref="RequestEnvelope"/> prepared by <see cref="PerformRemoteProcedureCallAsync"/>.</param>
        /// <param name="responseEnvelope">The <see cref="ResponseEnvelope"/> received from <see cref="SendRemoteProcedureCallAsync(POGOProtos.Networking.Requests.Request)" />.</param>
        /// <returns>Returns the <see cref="ByteString" />response of the <see cref="Request"/>.</returns>
        private ByteString HandleResponseEnvelope(RequestEnvelope requestEnvelope, ResponseEnvelope responseEnvelope)
        {
            //if (responseEnvelope.Returns.Count == 0)
            //{
            //    throw new Exception("There were 0 responses.");
            //}

            // Take requested response and remove from returns.
            var requestResponse = responseEnvelope.Returns[0];

            // Handle the default responses.
            HandleDefaultResponses(requestEnvelope, responseEnvelope.Returns);

            // Handle responses which affect the inventory
            HandleInventoryResponses(requestEnvelope.Requests[0], requestResponse);

            return requestResponse;
        }

        private void HandleInventoryResponses(Request request, ByteString requestResponse)
        {
            ulong pokemonId = 0;
            switch (request.RequestType)
            {
                case RequestType.ReleasePokemon:
                    var releaseResponse = ReleasePokemonResponse.Parser.ParseFrom(requestResponse);
                    if (releaseResponse.Result == ReleasePokemonResponse.Types.Result.Success ||
                        releaseResponse.Result == ReleasePokemonResponse.Types.Result.Failed)
                    {
                        var releaseMessage = ReleasePokemonMessage.Parser.ParseFrom(request.RequestMessage);
                        pokemonId = releaseMessage.PokemonId;
                    }
                    break;

                case RequestType.EvolvePokemon:
                    var evolveResponse = EvolvePokemonResponse.Parser.ParseFrom(requestResponse);
                    if (evolveResponse.Result == EvolvePokemonResponse.Types.Result.Success ||
                        evolveResponse.Result == EvolvePokemonResponse.Types.Result.FailedPokemonMissing)
                    {
                        var releaseMessage = ReleasePokemonMessage.Parser.ParseFrom(request.RequestMessage);
                        pokemonId = releaseMessage.PokemonId;
                    }
                    break;

                case RequestType.GetPlayer:
                    var getPlayerResponse = GetPlayerResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = getPlayerResponse.PlayerData;
                    _session.Player.Banned = getPlayerResponse.Banned;
                    _session.Player.Warn = getPlayerResponse.Warn;

                    if (getPlayerResponse.Warn)
                    {
                        _session.Logger.Warn("This account is flagged.");
                    }
                    if (getPlayerResponse.Banned)
                    {
                        throw new SessionStateException("This account is banned.");
                    }
                    break;

                case RequestType.SetPlayerTeam:
                    var setPlayerTeamResponse = SetPlayerTeamResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = setPlayerTeamResponse.PlayerData;
                    break;

                case RequestType.ClaimCodename:
                    var claimCodenameResponse = ClaimCodenameResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = claimCodenameResponse.UpdatedPlayer;
                    break;

                case RequestType.MarkTutorialComplete:
                    var markTutorialCompleteResponse = MarkTutorialCompleteResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = markTutorialCompleteResponse.PlayerData;
                    break;

                case RequestType.SetAvatar:
                    var setAvatarResponse = SetAvatarResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = setAvatarResponse.PlayerData;
                    break;

                case RequestType.SetContactSettings:
                    var setContactSettingsResponse = SetContactSettingsResponse.Parser.ParseFrom(requestResponse);
                    _session.Player.Data = setContactSettingsResponse.PlayerData;
                    break;
            }

            if (pokemonId > 0)
            {
                var pokemons = _session.Player.Inventory.InventoryItems.Where(
                    i =>
                        i?.InventoryItemData?.PokemonData != null &&
                        i.InventoryItemData.PokemonData.Id.Equals(pokemonId));
                _session.Player.Inventory.RemoveInventoryItems(pokemons);
            }
        }

        /// <summary>
        ///     Handles the default heartbeat responses.
        /// </summary>
        /// <param name="requestEnvelope"></param>
        /// <param name="returns">The payload of the <see cref="ResponseEnvelope" />.</param>
        private void HandleDefaultResponses(RequestEnvelope requestEnvelope, IList<ByteString> returns)
        {
            var responseIndexes = new Dictionary<int, RequestType>();

            for (var i = 0; i < requestEnvelope.Requests.Count; i++)
            {
                var request = requestEnvelope.Requests[i];
                if (_defaultRequests.Contains(request.RequestType))
                    responseIndexes.Add(i, request.RequestType);
                if (request.RequestType == RequestType.GetIncensePokemon && _session.IncenseUsed)
                    responseIndexes.Add(i, request.RequestType);
            }

            foreach (var responseIndex in responseIndexes)
            {
                var bytes = returns[responseIndex.Key];

                switch (responseIndex.Value)
                {
                    case RequestType.GetHatchedEggs: // Get_Hatched_Eggs
                        var hatchedEggs = GetHatchedEggsResponse.Parser.ParseFrom(bytes);
                        if (hatchedEggs.Success && hatchedEggs.PokemonId.Count > 0)
                        {
                            _session.OnHatchedEggsReceived(hatchedEggs);
                        }
                        break;

                    case RequestType.GetHoloholoInventory: // Get_Inventory
                        var inventory = GetHoloInventoryResponse.Parser.ParseFrom(bytes);
                        if (inventory.Success)
                        {
                            if (inventory.InventoryDelta.NewTimestampMs >=
                                _session.Player.Inventory.LastInventoryTimestampMs)
                            {
                                _session.Player.Inventory.LastInventoryTimestampMs =
                                    inventory.InventoryDelta.NewTimestampMs;
                                if (inventory.InventoryDelta != null &&
                                    inventory.InventoryDelta.InventoryItems.Count > 0)
                                {
                                    _session.Player.Inventory.UpdateInventoryItems(inventory.InventoryDelta);
                                }
                            }
                        }
                        break;

                    case RequestType.CheckAwardedBadges: // Check_Awarded_Badges
                        var awardedBadges = CheckAwardedBadgesResponse.Parser.ParseFrom(bytes);
                        if (awardedBadges.Success && awardedBadges.AwardedBadges.Count > 0)
                        {
                            _session.OnCheckAwardedBadgesReceived(awardedBadges);
                        }
                        break;

                    case RequestType.DownloadSettings: // Download_Settings
                        DownloadSettingsActionResponse downloadSettings = null;
                        try
                        {
                            downloadSettings = DownloadSettingsActionResponse.Parser.ParseFrom(bytes);
                        }
                        catch (Exception)
                        {
                            downloadSettings = new DownloadSettingsActionResponse() { Error = "Could not parse downloadSettings" };
                            continue;
                        }
                        if (string.IsNullOrEmpty(downloadSettings.Error))
                        {
                            if (downloadSettings.Settings == null)
                            {
                                continue;
                            }
                            if (_session.GlobalSettings == null || _session.GlobalSettingsHash != downloadSettings.Hash)
                            {
                                _session.GlobalSettingsHash = downloadSettings.Hash;
                                _session.GlobalSettings = downloadSettings.Settings;
                            }
                            else
                            {
                                _session.GlobalSettings = downloadSettings.Settings;
                            }
                        }
                        else
                        {
                            _session.Logger.Debug($"DownloadSettingsResponse.Error: '{downloadSettings.Error}'");
                            continue;
                        }
                        break;

                    case RequestType.CheckChallenge:
                        var checkChallenge = CheckChallengeResponse.Parser.ParseFrom(bytes);
                        if (checkChallenge.ShowChallenge)
                        {
                            _session.OnCaptchaReceived(checkChallenge.ChallengeUrl);
                            _session.Pause();
                            continue;
                        }
                        break;

                    case RequestType.GetBuddyWalked:
                        var getBuddyWalked = GetBuddyWalkedResponse.Parser.ParseFrom(bytes);
                        if (getBuddyWalked.Success && getBuddyWalked.CandyEarnedCount > 0)
                        {
                            _session.OnBuddyWalked(getBuddyWalked);
                        }
                        break;

                    case RequestType.GetIncensePokemon:
                        var getIncensePokemonResponse = GetIncensePokemonResponse.Parser.ParseFrom(bytes);
                        _session.Map.IncensePokemon = null;
                        if (getIncensePokemonResponse != null)
                        {
                            var pokemon = new MapPokemon
                            {
                                EncounterId = getIncensePokemonResponse.EncounterId,
                                ExpirationTimestampMs = getIncensePokemonResponse.DisappearTimestampMs,
                                Latitude = getIncensePokemonResponse.Latitude,
                                Longitude = getIncensePokemonResponse.Longitude,
                                PokemonId = getIncensePokemonResponse.PokemonId,
                                SpawnPointId = getIncensePokemonResponse.EncounterLocation,
                                PokemonDisplay = getIncensePokemonResponse.PokemonDisplay
                            };

                            if (pokemon.PokemonId != PokemonId.Missingno)
                                _session.Logger.Debug($"Received Incense Pokemon {pokemon.PokemonId.ToString()}");
                            _session.Map.IncensePokemon = pokemon;
                        }
                        break;

                    case RequestType.GetInbox:
                        var getInboxResponse = GetInboxResponse.Parser.ParseFrom(bytes);
                        if (getInboxResponse.Inbox.Notifications.Count > 0)
                        {
                            _session.OnInboxNotification(getInboxResponse);
                        }
                        break;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        internal void Dispose(bool disposing)
        {
            if (!disposing) return;
            _rpcQueueMutex?.Dispose();
        }

        private async Task DownloadRemoteConfig()
        {
            var request = new Request
            {
                RequestType = RequestType.DownloadRemoteConfigVersion,
                RequestMessage = new DownloadRemoteConfigVersionMessage
                {
                    Platform = GetPlatform(),
                    DeviceManufacturer = _session.Device.DeviceInfo.HardwareManufacturer,
                    DeviceModel = _session.Device.DeviceInfo.DeviceModel,
                    Locale = _session.Player.PlayerLocale.Language + "_" + _session.Player.PlayerLocale.Country,
                    AppVersion = Configuration.Hasher.AppVersion
                }.ToByteString()
            };

            ByteString response = await SendRemoteProcedureCallAsync(request, true, true, true);

            if (response != null)
            {
                var downloadRemoteConfigVersionMessage = DownloadRemoteConfigVersionResponse.Parser.ParseFrom(response);

                if (downloadRemoteConfigVersionMessage.Result == DownloadRemoteConfigVersionResponse.Types.Result.Success)
                {
                    _session.Templates.AssetDigestTimestampMs = downloadRemoteConfigVersionMessage.AssetDigestTimestampMs;
                    _session.Templates.ItemTemplatesTimestampMs = downloadRemoteConfigVersionMessage.ItemTemplatesTimestampMs;
                    _session.Templates.LocalConfigVersion = downloadRemoteConfigVersionMessage;

                    if (_session.ManageRessources)
                        _session.OnRemoteConfigVersionReceived(downloadRemoteConfigVersionMessage);
                }
            }
        }

        private async Task DownloadItemTemplates(int pageOffset = 0, ulong timestamp = 0ul)
        {
            if (_session.Templates.ItemTemplates != null)
            {
                _session.Logger.Debug("Use cached values for DownloadItemTemplates");
                return;
            }

            var time = timestamp != 0ul ? timestamp : _session.Templates.ItemTemplatesTimestampMs;
            var templates = new List<DownloadItemTemplatesResponse.Types.ItemTemplate>();
            var local_config_version = new DownloadRemoteConfigVersionResponse();
            int page = pageOffset > 0 ? pageOffset : 0;

            if (_session.Templates.LocalConfigVersion != null)
                local_config_version = _session.Templates.LocalConfigVersion;

            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.DownloadItemTemplates,
                RequestMessage = new DownloadItemTemplatesMessage
                {
                    // PageOffset = page,
                    // Paginate = true,
                    // PageTimestamp = time
                }.ToByteString()
            }, true, true, true);

            if (response != null)
            {
                var downloadItemTemplatesResponse = DownloadItemTemplatesResponse.Parser.ParseFrom(response);

                switch (downloadItemTemplatesResponse.Result)
                {
                    case DownloadItemTemplatesResponse.Types.Result.Success:
                        // Success?!
                        // Comment next line if you uses paginate true
                        templates.AddRange(downloadItemTemplatesResponse.ItemTemplates);
                        break;
                    case DownloadItemTemplatesResponse.Types.Result.Page:
                        // Pages is here xelwon
                        // here if paginate recuperate values for current page
                        // _session.Logger.Debug(String.Format("Page Templates: {0}", downloadItemTemplatesResponse.PageOffset));
                        // templates.AddRange(downloadItemTemplatesResponse.ItemTemplates);
                        break;
                    case DownloadItemTemplatesResponse.Types.Result.Retry:
                        // Retry re-send request
                        await DownloadItemTemplates(downloadItemTemplatesResponse.PageOffset, downloadItemTemplatesResponse.TimestampMs);
                        break;
                    case DownloadItemTemplatesResponse.Types.Result.Unset:
                        break;
                }

                _session.Templates.ItemTemplates = templates;

                if (_session.ManageRessources)
                    _session.OnItemTemplatesReceived(templates);
            }
        }

        private void HandleEventHandler(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs errorArgs)
        {
            var currentError = errorArgs.ErrorContext.Error.Message;
            errorArgs.ErrorContext.Handled = true;
        }

        private async Task GetAssetDigest(int pageOffset = 0, ulong timestamp = 0ul)
        {
            if (_session.Templates.AssetDigests != null)
            {
                _session.Logger.Debug("Use cached values for GetAssetDigest");
                return;
            }

            var time = timestamp != 0ul ? timestamp : _session.Templates.AssetDigestTimestampMs;
            var digests = new List<POGOProtos.Data.AssetDigestEntry>();
            var local_config_version = new DownloadRemoteConfigVersionResponse();
            int page = pageOffset > 0 ? pageOffset : 0;

            if (_session.Templates.LocalConfigVersion != null)
                local_config_version = _session.Templates.LocalConfigVersion;

            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.GetAssetDigest,
                RequestMessage = new GetAssetDigestMessage
                {
                    Platform = GetPlatform(),
                    DeviceManufacturer = _session.Device.DeviceInfo.HardwareManufacturer,
                    DeviceModel = _session.Device.DeviceInfo.DeviceModel,
                    Locale = _session.Player.PlayerLocale.Language + "_" + _session.Player.PlayerLocale.Country,
                    AppVersion = Configuration.Hasher.AppVersion,
                    //PageOffset = page,
                    //Paginate = true,
                    //PageTimestamp = time
                }.ToByteString()
            }, true, true, true);

            if (response != null)
            {
                var getAssetDigestResponse = GetAssetDigestResponse.Parser.ParseFrom(response);

                switch (getAssetDigestResponse.Result)
                {
                    case GetAssetDigestResponse.Types.Result.Success:
                        // Success?!
                        // Comment next line if you uses paginate true
                        digests.AddRange(getAssetDigestResponse.Digest);
                        break;
                    case GetAssetDigestResponse.Types.Result.Retry:
                        // Retry re-send request
                        await GetAssetDigest(getAssetDigestResponse.PageOffset, getAssetDigestResponse.TimestampMs);
                        break;
                    case GetAssetDigestResponse.Types.Result.Page:
                        // Pages is here xelwon
                        // here if paginate recuperate values for current page
                        // _session.Logger.Debug(String.Format("Page AssetDigest: {0}", getAssetDigestResponse.PageOffset));
                        // digests.AddRange(getAssetDigestResponse.Digest);
                        break;
                    case GetAssetDigestResponse.Types.Result.Unset:
                        _session.SetTemporalBan();
                        throw new SessionStateException(nameof(response));
                }

                _session.Templates.AssetDigests = digests;

                if (_session.ManageRessources)
                    _session.OnAssetDigestReceived(digests);
            }
        }

        private async Task GetDownloadURLs()
        {
            var toCheck = new[] {
                "i18n_general",
                "i18n_moves",
                "i18n_items"
            };

            await GetDownloadURLs(toCheck);
        }

        private async Task GetDownloadURLs(string[] toCheck)
        {
            if (_session.Templates.DownloadUrls != null)
            {
                _session.Logger.Debug("Use cached values for GetDownloadUrls");
                return;
            }

            if (_session.Templates.AssetDigests == null)
            {
                _session.Logger.Warn("AssetDigests values needed for GetDownloadUrls is empty, skip request.");
                return;
            }

            var dowloadUrls = new List<POGOProtos.Data.DownloadUrlEntry>();
            var toDownload = new List<string>();

            for (int i = 0; i < _session.Templates.AssetDigests.Count; i++)
            {
                var digest = _session.Templates.AssetDigests[i];
                foreach (var check in toCheck)
                {
                    if (digest.BundleName == check)
                        toDownload.Add(digest.AssetId);
                }
            }

            if (toDownload.Count == 0)
            {
                _session.Logger.Debug("Urls to load is empty, ignore request.");
                return;
            }

            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.GetDownloadUrls,
                RequestMessage = new GetDownloadUrlsMessage
                {
                    AssetId = { toDownload.ToArray() }
                }.ToByteString()
            }, true, true, true);

            if (response != null)
            {
                var getDownloadUrlsResponse = GetDownloadUrlsResponse.Parser.ParseFrom(response);
                dowloadUrls.AddRange(getDownloadUrlsResponse.DownloadUrls);
                _session.Templates.DownloadUrls = dowloadUrls;

                if (_session.ManageRessources)
                    _session.OnUrlsReceived(dowloadUrls);
            }
        }

        private async Task<RepeatedField<string>> FetchAllNews()
        {
            RepeatedField<string> NewsIds = new RepeatedField<string>();

            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.FetchAllNews,
                RequestMessage = new FetchAllNewsMessage
                {
                    //
                }.ToByteString()
            }, false, false, false);

            if (response != null)
            {
                var fetchAllNewsResponse = FetchAllNewsResponse.Parser.ParseFrom(response);

                switch (fetchAllNewsResponse.Result)
                {
                    case FetchAllNewsResponse.Types.Result.NoNewsFound:
                        break;
                    case FetchAllNewsResponse.Types.Result.Success:
                        if (fetchAllNewsResponse.CurrentNews.NewsArticles.Count > 0)
                        {
                            foreach (var art in fetchAllNewsResponse.CurrentNews.NewsArticles)
                            {
                                NewsIds.Add(art.Id);
                            }

                            return NewsIds;
                        }
                        break;
                    case FetchAllNewsResponse.Types.Result.Unset:
                        break;
                };
            }
            return NewsIds;
        }

        private async Task MarkReadNewsArticle(RepeatedField<string> NewsIds)
        {
            var response = await SendRemoteProcedureCallAsync(new Request
            {
                RequestType = RequestType.MarkReadNewsArticle,
                RequestMessage = new MarkReadNewsArticleMessage
                {
                    NewsIds = { NewsIds }
                }.ToByteString()
            }, false, false, false);

            if (response != null)
            {
                var markReadNewsArticleResponse = MarkReadNewsArticleResponse.Parser.ParseFrom(response);

                switch (markReadNewsArticleResponse.Result)
                {
                    case MarkReadNewsArticleResponse.Types.Result.NoNewsFound:
                        break;
                    case MarkReadNewsArticleResponse.Types.Result.Success:
                        _session.Logger.Info("MarkReadNewsArticle success.");
                        break;
                    case MarkReadNewsArticleResponse.Types.Result.Unset:
                        break;
                };
            }
        }
    }
}
