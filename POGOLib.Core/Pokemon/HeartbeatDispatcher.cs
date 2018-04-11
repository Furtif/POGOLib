using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.Collections;
using POGOLib.Official.Exceptions;
using POGOLib.Official.Net;
using POGOProtos.Map;

namespace POGOLib.Official.Pokemon
{
    internal class HeartbeatDispatcher
    {
        /// <summary>
        ///     The authenticated <see cref="Session" />.
        /// </summary>
        private readonly Session _session;

        /// <summary>
        ///     Determines whether we can keep heartbeating.
        /// </summary>
        private CancellationTokenSource _heartbeatCancellation;

        private Task _heartbeatTask;

        internal HeartbeatDispatcher(Session session)
        {
            _session = session;
        }

        /// <summary>
        ///     Checks every second if we need to update.
        /// </summary>
        private async Task CheckDispatch(TaskCompletionSource<bool> firstRefreshCompleted)
        {
            while (!_heartbeatCancellation.IsCancellationRequested && (_session.State == SessionState.Started || _session.State == SessionState.Resumed))
            {
                var canRefresh = false;
                if (_session.GlobalSettings != null)
                {
                    var minSeconds = _session.GlobalSettings.MapSettings.GetMapObjectsMinRefreshSeconds;
                    var maxSeconds = _session.GlobalSettings.MapSettings.GetMapObjectsMaxRefreshSeconds;
                    var minDistance = _session.GlobalSettings.MapSettings.GetMapObjectsMinDistanceMeters;
                    var lastGeoCoordinate = _session.RpcClient.LastGeoCoordinateMapObjectsRequest;
                    var secondsSinceLast = DateTime.UtcNow.Subtract(_session.RpcClient.LastRpcMapObjectsRequest).TotalSeconds;// Seconds;

                    if (lastGeoCoordinate.IsUnknown)
                    {
                        _session.Logger.Debug("Refreshing MapObjects, reason: 'lastGeoCoordinate.IsUnknown'.");
                        canRefresh = true;
                    }
                    else if (secondsSinceLast >= minSeconds)
                    {
                        var metersMoved = _session.Player.Coordinate.GetDistanceTo(lastGeoCoordinate);
                        if (secondsSinceLast >= maxSeconds)
                        {
                            _session.Logger.Debug($"Refreshing MapObjects, reason: 'secondsSinceLast({secondsSinceLast}) >= maxSeconds({maxSeconds})'.");
                            canRefresh = true;
                        }
                        else if (metersMoved >= minDistance)
                        {
                            _session.Logger.Debug($"Refreshing MapObjects, reason: 'metersMoved({metersMoved}) >= minDistance({minDistance})'.");
                            canRefresh = true;
                        }
                    }
                }
                else
                {
                    canRefresh = true;
                }

                try
                {
                    if (canRefresh)
                    {
                        await Dispatch();
                    }
                }
                // cancelled
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    //Null
                }
                finally
                {
                    // after dispatch, signal as complete
                    firstRefreshCompleted?.TrySetResult(true);
                    firstRefreshCompleted = null;
                    await Task.Delay(TimeSpan.FromMilliseconds(1000), _heartbeatCancellation.Token);
                }
            }

            _session.Map.Cells = new RepeatedField<MapCell>();
            firstRefreshCompleted?.TrySetResult(false);
            _session.Logger.Debug("Heartbeat got cancelled");
        }

        internal async Task StartDispatcherAsync()
        {
            if (_heartbeatTask != null)
            {
                throw new SessionStateException("Heartbeat task already running");
            }

            var firstRefreshCompleted = new TaskCompletionSource<bool>();
            _heartbeatCancellation = new CancellationTokenSource();
            _heartbeatTask = CheckDispatch(firstRefreshCompleted);

            // wait for first heartbeat to complete
            await firstRefreshCompleted.Task;
        }

        internal void StopDispatcher()
        {
            _heartbeatCancellation?.Cancel();
            _heartbeatTask = null;
        }

        private async Task Dispatch()
        {
            await _session.RpcClient.RefreshMapObjectsAsync();
        }
    }
}