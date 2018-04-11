using System;
using System.Linq;
using POGOLib.Official.Extensions;
using POGOLib.Official.Net;
using POGOProtos.Data;
using POGOProtos.Data.Player;
using POGOProtos.Networking.Requests.Messages;

namespace POGOLib.Official.Pokemon
{
    public class Player
    {
        internal Player(Session session, GeoCoordinate coordinate, GetPlayerMessage.Types.PlayerLocale playerLocale)
        {
            Coordinate = coordinate;
            PlayerLocale = playerLocale;
            Inventory = new Inventory(session);
            session.InventoryUpdate += InventoryOnUpdate;
        }

        /// <summary>
        ///     Gets the <see cref="GeoCoordinate" /> of the <see cref="Player" />.
        ///     Used for internal calculations.
        /// </summary>
        internal GeoCoordinate Coordinate { get; private set; }

        /// <summary>
        ///     Gets the <see cref="Player" /> his current latitude.
        /// </summary>
        public double Latitude
        {
            get { return Coordinate.Latitude; }
            set { Coordinate.Latitude = value; }
        }

        /// <summary>
        ///     Gets the <see cref="Player" /> his current longitude.
        /// </summary>
        public double Longitude
        {
            get { return Coordinate.Longitude; }
            set { Coordinate.Longitude = value; }
        }

        /// <summary>
        ///     Gets the <see cref="Player" /> his current altitude.
        /// </summary>
        public double Altitude
        {
            get { return Coordinate.Altitude; }
            set { Coordinate.Altitude = value; }
        }

        /// <summary>
        ///     Gets the <see cref="Inventory" /> of the <see cref="Player" />
        /// </summary>
        public Inventory Inventory { get; }

        /// <summary>
        ///     Gets the <see cref="Stats" /> of the beautiful <see cref="Inventory" /> object by PokémonGo.
        /// </summary>
        public PlayerStats Stats { get; private set; }

        /// <summary>
        ///     Gets the <see cref="PlayerLocale" /> of the <see cref="Player" />
        /// </summary>
        internal GetPlayerMessage.Types.PlayerLocale PlayerLocale { get; private set; }

        /// <summary>
        ///     Gets the <see cref="Banned" /> of the <see cref="Player" />
        /// </summary>
        public bool Banned { get; internal set; }

        /// <summary>
        ///     Gets the <see cref="Warn" /> of the <see cref="Player" />
        /// </summary>
        public bool Warn { get; internal set; }

        public PlayerData Data { get; set; }

        /// <summary>
        ///     Sets the <see cref="GeoCoordinate" /> of the <see cref="Player" />.
        /// </summary>
        /// <param name="latitude">The latitude of your location.</param>
        /// <param name="longitude">The longitude of your location.</param>
        /// <param name="altitude">The altitude of your location.</param>
        public void SetCoordinates(double latitude, double longitude, double altitude = 100)
        {
            if (altitude != 100 && altitude > 0)
            {
                Coordinate = new GeoCoordinate(latitude, longitude, altitude);
                Altitude = altitude;
                return;
            }

            Coordinate = new GeoCoordinate(latitude, longitude);
            Altitude = Coordinate.Altitude;

            //make fake altitude
            if (Altitude == 0)
            {
                var randAlt = new Random();
                Altitude = randAlt.NextDouble(5, 45);
                Coordinate = new GeoCoordinate(latitude, longitude, Altitude);
            }
        }

        /// <summary>
        ///     Sets the <see cref="GeoCoordinate" /> of the <see cref="Player" />.
        /// </summary>
        /// <param name="coordinate">The coordinate of your location.</param>
        public void SetCoordinates(GeoCoordinate coordinate)
        {
            Coordinate = coordinate;
            Altitude = coordinate.Altitude;

            //make fake altitude
            if (Altitude == 0)
            {
                var randAlt = new Random();
                Altitude = randAlt.NextDouble(5, 45);
                Coordinate = new GeoCoordinate(coordinate.Latitude, coordinate.Longitude, Altitude);
            }
        }

        /// <summary>
        ///     Calculates the distance between the <see cref="Player" /> his current <see cref="Coordinate" /> and the given
        ///     coordinate.
        /// </summary>
        /// <param name="latitude">The latitude.</param>
        /// <param name="longitude">The longitude.</param>
        /// <returns>Returns distance in meters.</returns>
        public double DistanceTo(double latitude, double longitude)
        {
            return Coordinate.GetDistanceTo(new GeoCoordinate(latitude, longitude));
        }

        private void InventoryOnUpdate(object sender, EventArgs eventArgs)
        {
            var stats =
                Inventory.InventoryItems.FirstOrDefault(i => i?.InventoryItemData?.PlayerStats != null)?
                    .InventoryItemData?.PlayerStats;
            if (stats != null)
            {
                Stats = stats;
            }
        }
    }
}