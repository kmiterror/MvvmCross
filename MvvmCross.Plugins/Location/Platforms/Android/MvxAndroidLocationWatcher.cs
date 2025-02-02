﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MS-PL license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using Android.Content;
using Android.Locations;
using Android.OS;
using Microsoft.Extensions.Logging;
using MvvmCross.Exceptions;
using MvvmCross.Logging;
using MvvmCross.Platforms.Android;

namespace MvvmCross.Plugin.Location.Platforms.Android
{
    [Preserve(AllMembers = true)]
    public sealed class MvxAndroidLocationWatcher
        : MvxLocationWatcher, IMvxLocationReceiver
    {
        private Context _context;
        private LocationManager _locationManager;
        private readonly MvxLocationListener _locationListener;
        private string _bestProvider;

        public MvxAndroidLocationWatcher()
        {
            EnsureStopped();
            _locationListener = new MvxLocationListener(this);
        }

        private Context Context => _context ?? (_context = Mvx.IoCProvider.Resolve<IMvxAndroidGlobals>().ApplicationContext);

        protected override void PlatformSpecificStart(MvxLocationOptions options)
        {
            if (_locationManager != null)
                throw new MvxException("You cannot start the MvxLocation service more than once");

            _locationManager = (LocationManager)Context.GetSystemService(Context.LocationService);
            if (_locationManager == null)
            {
                MvxPluginLog.Instance?.Log(LogLevel.Warning, "Location Service Manager unavailable - returned null");
                SendError(MvxLocationErrorCode.ServiceUnavailable);
                return;
            }
            var criteria = new Criteria()
            {
                Accuracy = options.Accuracy == MvxLocationAccuracy.Fine ? Accuracy.Fine : Accuracy.Coarse
            };
            _bestProvider = _locationManager.GetBestProvider(criteria, true);
            if (_bestProvider == null)
            {
                MvxPluginLog.Instance?.Log(LogLevel.Warning, "Location Service Provider unavailable - returned null");
                SendError(MvxLocationErrorCode.ServiceUnavailable);
                return;
            }

            _locationManager.RequestLocationUpdates(
                _bestProvider,
                (long)options.TimeBetweenUpdates.TotalMilliseconds,
                options.MovementThresholdInM,
                _locationListener);

            Permission = _locationManager.IsProviderEnabled(_bestProvider)
                ? MvxLocationPermission.Granted
                : MvxLocationPermission.Denied;
        }

        protected override void PlatformSpecificStop()
        {
            EnsureStopped();
        }

        private void EnsureStopped()
        {
            if (_locationManager != null)
            {
                _locationManager.RemoveUpdates(_locationListener);
                _locationManager = null;
                _bestProvider = null;
            }
        }

        private static MvxGeoLocation CreateLocation(global::Android.Locations.Location androidLocation)
        {
            var position = new MvxGeoLocation { Timestamp = androidLocation.Time.FromMillisecondsUnixTimeToUtc() };
            var coords = position.Coordinates;

            if (androidLocation.HasAltitude)
                coords.Altitude = androidLocation.Altitude;

            if (androidLocation.HasBearing)
                coords.Heading = androidLocation.Bearing;

            coords.Latitude = androidLocation.Latitude;
            coords.Longitude = androidLocation.Longitude;
            if (androidLocation.HasSpeed)
                coords.Speed = androidLocation.Speed;
            if (androidLocation.HasAccuracy)
            {
                coords.Accuracy = androidLocation.Accuracy;
            }

            return position;
        }

        public override MvxGeoLocation CurrentLocation
        {
            get
            {
                if (_locationManager == null || _bestProvider == null)
                    throw new MvxException("Location Manager not started");

                var androidLocation = _locationManager.GetLastKnownLocation(_bestProvider);
                if (androidLocation == null)
                    return null;

                return CreateLocation(androidLocation);
            }
        }

        #region Implementation of ILocationListener

        public void OnLocationChanged(global::Android.Locations.Location androidLocation)
        {
            if (androidLocation == null)
            {
                MvxPluginLog.Instance?.Log(LogLevel.Trace, "Android: Null location seen");
                return;
            }

            if (androidLocation.Latitude == double.MaxValue
                || androidLocation.Longitude == double.MaxValue)
            {
                MvxPluginLog.Instance?.Log(LogLevel.Trace, "Android: Invalid location seen");
                return;
            }

            MvxGeoLocation location;
            try
            {
                location = CreateLocation(androidLocation);
            }
            catch (ThreadAbortException)
            {
                throw;
            }
            catch (Exception exception)
            {
                MvxPluginLog.Instance?.Log(LogLevel.Trace, "Android: Exception seen in converting location " + exception.ToLongString());
                return;
            }

            SendLocation(location);
        }

        public void OnProviderDisabled(string provider)
        {
            Permission = MvxLocationPermission.Denied;
            SendError(MvxLocationErrorCode.ServiceUnavailable);
        }

        public void OnProviderEnabled(string provider)
        {
            Permission = MvxLocationPermission.Granted;
        }

        public void OnStatusChanged(string provider, Availability status, Bundle extras)
        {
            switch (status)
            {
                case Availability.Available:
                    break;
                case Availability.OutOfService:
                    SendError(MvxLocationErrorCode.ServiceUnavailable);
                    break;
                case Availability.TemporarilyUnavailable:
                    SendError(MvxLocationErrorCode.PositionUnavailable);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        #endregion
    }
}
