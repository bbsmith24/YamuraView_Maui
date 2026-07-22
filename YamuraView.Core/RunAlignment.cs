namespace YamuraView.Core
{
    /// <summary>
    /// Start-position based run alignment. The user marks the start position once, on the
    /// first loaded run (alignment wizard); <see cref="SetStartPosition"/> captures the GPS
    /// location and aligned time/distance of that mark. Every run loaded afterward goes
    /// through <see cref="AlignToStartPosition"/>: its GPS point nearest the marked location
    /// is found, and the run's Time/Distance offsets are set so that point lands on the
    /// start position's aligned time/distance. Replaces the automatic AlignGPS/AlignTime
    /// pass that used to run during parsing.
    /// </summary>
    public static class RunAlignment
    {
        /// <summary>
        /// Records the start position from a mark the user placed on <paramref name="run"/>
        /// (display space, on the Time axis or a distance channel), and updates
        /// <see cref="DataLogger.DistanceAlignPoint"/> so Delta-T computes from the start
        /// position. Returns a warning if the run has no GPS data - the position is still
        /// stored (Delta-T still works), but later runs can't be auto-aligned to it.
        /// </summary>
        public static string? SetStartPosition(DataLogger dataLogger, RunData run, string axisChannel, bool axisIsTime, float alignedMark)
        {
            // mark -> the run's raw timestamp
            float rawTime;
            if (axisIsTime)
            {
                rawTime = alignedMark - run.TimeOffset;
            }
            else
            {
                float? timeAtMark = TimeAtDistance(run, axisChannel, alignedMark - run.DistanceOffset);
                if (!timeAtMark.HasValue)
                {
                    return $"{run.runName}: no {axisChannel} data at the start position mark.";
                }
                rawTime = timeAtMark.Value;
            }

            float? rawDistance = DistanceAtTime(run, rawTime);
            (float Lat, float Long)? gps = GpsAtTime(run, rawTime);

            dataLogger.RunStartPosition = new StartPosition
            {
                Latitude = gps?.Lat ?? float.NaN,
                Longitude = gps?.Long ?? float.NaN,
                AlignedTime = rawTime + run.TimeOffset,
                AlignedDistance = rawDistance.HasValue ? rawDistance.Value + run.DistanceOffset : float.NaN
            };
            // Delta-T only computes from the start position on
            dataLogger.DistanceAlignPoint = dataLogger.RunStartPosition.AlignedDistance;

            return gps == null
                ? $"{run.runName}: no GPS data - runs loaded later can't be aligned to this start position automatically."
                : null;
        }

        /// <summary>
        /// Aligns <paramref name="run"/> to the defined start position: finds the run's GPS
        /// point nearest the marked location and sets the run's Time (and, when distance
        /// data exists, Distance) offset so that point lands on the start position. Returns
        /// a warning - and leaves the offsets unchanged - if there's no usable start
        /// position or the run has no GPS data.
        /// </summary>
        public static string? AlignToStartPosition(DataLogger dataLogger, RunData run)
        {
            StartPosition? anchor = dataLogger.RunStartPosition;
            if (anchor == null)
            {
                return $"{run.runName}: no start position defined yet.";
            }
            if (float.IsNaN(anchor.Latitude) || float.IsNaN(anchor.Longitude))
            {
                return $"{run.runName}: the start position has no GPS location, can't auto-align.";
            }
            if (!run.channels.TryGetValue("Latitude", out DataChannel? latChannel) ||
                !run.channels.TryGetValue("Longitude", out DataChannel? longChannel) ||
                latChannel.DataPoints.Count == 0 || longChannel.DataPoints.Count == 0)
            {
                return $"{run.runName}: no GPS data, can't align to the start position.";
            }

            // nearest GPS point to the start position
            float minDistance = float.MaxValue;
            float matchedTime = float.NaN;
            foreach (KeyValuePair<float, float> latPoint in latChannel.DataPoints)
            {
                if (!longChannel.DataPoints.TryGetValue(latPoint.Key, out float longVal))
                {
                    continue; // no longitude sample at this timestamp
                }
                float distance = GPSDistance(anchor.Latitude, anchor.Longitude, latPoint.Value, longVal);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    matchedTime = latPoint.Key;
                }
            }
            if (float.IsNaN(matchedTime))
            {
                return $"{run.runName}: latitude/longitude samples never line up, can't align.";
            }

            run.TimeOffset = anchor.AlignedTime - matchedTime;
            float? distanceAtMatch = DistanceAtTime(run, matchedTime);
            if (!float.IsNaN(anchor.AlignedDistance) && distanceAtMatch.HasValue)
            {
                run.DistanceOffset = anchor.AlignedDistance - distanceAtMatch.Value;
            }
            return null;
        }

        /// <summary>Raw timestamp of the sample whose distance value is closest to
        /// <paramref name="rawDistance"/> on the given distance channel (keyed time ->
        /// distance), or null if the channel is missing/empty. Linear scan: distance
        /// plateaus while the car is stationary, so the values aren't strictly sorted.</summary>
        private static float? TimeAtDistance(RunData run, string distanceChannel, float rawDistance)
        {
            if (!run.channels.TryGetValue(distanceChannel, out DataChannel? channel) || channel.DataPoints.Count == 0)
            {
                return null;
            }
            float bestDiff = float.MaxValue;
            float bestTime = float.NaN;
            foreach (KeyValuePair<float, float> point in channel.DataPoints)
            {
                float diff = Math.Abs(point.Value - rawDistance);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestTime = point.Key;
                }
            }
            return bestTime;
        }

        /// <summary>Raw distance at the sample nearest <paramref name="rawTime"/>, from the
        /// interpolated Distance channel (falling back to Distance-GPS), or null if the run
        /// has no distance data.</summary>
        private static float? DistanceAtTime(RunData run, float rawTime)
        {
            foreach (string name in new[] { "Distance", "Distance-GPS" })
            {
                if (run.channels.TryGetValue(name, out DataChannel? channel) && channel.DataPoints.Count > 0)
                {
                    return channel.DataPoints.Values[NearestKeyIndex(channel.DataPoints, rawTime)];
                }
            }
            return null;
        }

        /// <summary>GPS location at the sample nearest <paramref name="rawTime"/>, or null
        /// if the run has no latitude/longitude data.</summary>
        private static (float Lat, float Long)? GpsAtTime(RunData run, float rawTime)
        {
            if (!run.channels.TryGetValue("Latitude", out DataChannel? latChannel) ||
                !run.channels.TryGetValue("Longitude", out DataChannel? longChannel) ||
                latChannel.DataPoints.Count == 0 || longChannel.DataPoints.Count == 0)
            {
                return null;
            }
            int latIdx = NearestKeyIndex(latChannel.DataPoints, rawTime);
            float latTime = latChannel.DataPoints.Keys[latIdx];
            float latVal = latChannel.DataPoints.Values[latIdx];
            float longVal = longChannel.DataPoints.TryGetValue(latTime, out float exact)
                ? exact
                : longChannel.DataPoints.Values[NearestKeyIndex(longChannel.DataPoints, latTime)];
            return (latVal, longVal);
        }

        /// <summary>Index of the sample whose key is nearest <paramref name="key"/> (keys
        /// are sorted, so binary search).</summary>
        private static int NearestKeyIndex(SortedList<float, float> points, float key)
        {
            IList<float> keys = points.Keys;
            int lo = 0;
            int hi = keys.Count; // first index with keys[index] >= key
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            if (lo >= keys.Count)
            {
                return keys.Count - 1;
            }
            if (lo == 0)
            {
                return 0;
            }
            return key - keys[lo - 1] <= keys[lo] - key ? lo - 1 : lo;
        }

        /// <summary>
        /// great circle (haversine) distance between 2 lat/long points, in feet.
        /// </summary>
        private static float GPSDistance(float lat1Deg, float long1Deg, float lat2Deg, float long2Deg)
        {
            double R = 6371e3F; // meters
            R *= 3.28084; // feet
            double phi1 = DegreesToRadians(lat1Deg);
            double phi2 = DegreesToRadians(lat2Deg);
            double long1 = DegreesToRadians(long1Deg);
            double long2 = DegreesToRadians(long2Deg);
            double delta_phi = phi2 - phi1;
            double delta_lambda = long2 - long1;

            double a = Math.Pow(Math.Sin(delta_phi / 2), 2) +
                       Math.Cos(phi1) * Math.Cos(phi2) *
                       Math.Pow(Math.Sin(delta_lambda / 2), 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            double d = R * c;
            return (float)d;
        }

        private static float DegreesToRadians(double deg)
        {
            double rad = (deg * Math.PI) / 180.0;
            return (float)rad;
        }
    }
}
