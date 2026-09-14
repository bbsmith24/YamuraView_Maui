namespace YamuraView.Core
{
    /// <summary>A run's GPS track passing through a <see cref="TrackLine"/>: the interpolated
    /// crossing time and position (between the two GPS samples that straddle the line).</summary>
    public sealed record LineCrossing(TrackLine Line, float Time, double Latitude, double Longitude);

    /// <summary>One sector line's crossing within a lap: which sector, when it was crossed, and
    /// the elapsed time from the lap's start-line crossing.</summary>
    public sealed record SectorSplit(int Order, float CrossTime, float SplitFromLapStart);

    /// <summary>Timing for one lap (point-to-point runs produce exactly one, Start to Finish;
    /// circuit runs produce one per start-line crossing). <see cref="EndTime"/>/<see cref="LapTime"/>
    /// are null for an unfinished lap (start crossed but the closing line never was).</summary>
    public sealed class LapTiming
    {
        public int LapNumber { get; set; }
        public float StartTime { get; set; }
        public float? EndTime { get; set; }
        public float? LapTime => EndTime.HasValue ? EndTime.Value - StartTime : null;
        public List<SectorSplit> Sectors { get; } = new();
    }

    /// <summary>All timing derived from running one run against a track map.</summary>
    public sealed class RunTiming
    {
        public bool IsCircuit { get; set; }
        public List<LapTiming> Laps { get; } = new();
        /// <summary>The start-line crossing time of the first lap, or null if the run never
        /// crossed the start line. This is the precise alignment anchor Phase 2 uses.</summary>
        public float? FirstStartTime => Laps.Count > 0 ? Laps[0].StartTime : null;
    }

    /// <summary>
    /// The crossing/timing math for a <see cref="TrackMap"/>. Everything here is pure geometry
    /// over a run's GPS channels, so it runs identically on a GPS-equipped device and on a
    /// no-GPS analysis machine loading a shared ".ytm". Positions are handled in a local
    /// east/north meters frame anchored at each line's center (an equirectangular projection,
    /// longitude scaled by cos(latitude)); over the tens-of-meters span of a timing line this
    /// is well within GPS accuracy. The dominant tolerance is the line's own
    /// <see cref="TrackLine.Width"/>, which absorbs GPS scatter and float coordinate precision.
    /// </summary>
    public static class TrackMapGeometry
    {
        private const double MetersPerDegreeLatitude = 111320.0;
        private const double FeetToMeters = 0.3048;
        // a run segment whose along-line direction cross-product is below this (relative to a
        // 1 m baseline) is treated as running parallel to the line - no clean crossing
        private const double ParallelEpsilon = 1e-9;

        /// <summary>The two endpoints of a line as GPS coordinates, for drawing.</summary>
        public static ((double Lat, double Lon) A, (double Lat, double Lon) B) GetLineEndpoints(TrackLine line, TrackMapUnits units)
        {
            double halfWidthMeters = ToMeters(line.Width, units) / 2.0;
            double theta = line.Heading * Math.PI / 180.0;
            // line runs perpendicular to travel: travel = (sinθ east, cosθ north),
            // so the line direction is (cosθ east, -sinθ north)
            double eastDir = Math.Cos(theta);
            double northDir = -Math.Sin(theta);
            double cosLat = CosLat(line.Latitude);

            (double Lat, double Lon) end(double sign)
            {
                double east = sign * halfWidthMeters * eastDir;
                double north = sign * halfWidthMeters * northDir;
                return (line.Latitude + north / MetersPerDegreeLatitude,
                        line.Longitude + east / (MetersPerDegreeLatitude * cosLat));
            }
            return (end(1.0), end(-1.0));
        }

        /// <summary>
        /// Every crossing of any of the map's lines by the run, in time order. A line may be
        /// crossed more than once (circuit laps); each crossing is reported. Only crossings in
        /// the line's travel direction count.
        /// </summary>
        public static List<LineCrossing> FindCrossings(RunData run, TrackMap map)
        {
            List<LineCrossing> crossings = new();
            List<(float Time, double Lat, double Lon)> track = GetTrackPoints(run);
            if (track.Count < 2)
            {
                return crossings;
            }
            foreach (TrackLine line in map.Lines)
            {
                AddCrossingsForLine(track, line, map.Units, crossings);
            }
            crossings.Sort((a, b) => a.Time.CompareTo(b.Time));
            return crossings;
        }

        /// <summary>
        /// Builds the lap/sector timing for the run against the map. Point-to-point maps
        /// (<see cref="TrackMap.SameStartFinish"/> false) yield a single lap from the first
        /// Start crossing to the first Finish crossing after it; circuit maps yield one lap per
        /// Start crossing (the last one left open). Sector splits are the first crossing of each
        /// sector line inside a lap's window, in <see cref="TrackLine.Order"/> order.
        /// </summary>
        public static RunTiming BuildTiming(RunData run, TrackMap map)
        {
            RunTiming timing = new() { IsCircuit = map.SameStartFinish };
            List<LineCrossing> crossings = FindCrossings(run, map);

            List<float> startTimes = crossings.Where(c => c.Line.Type == LineType.Start)
                                              .Select(c => c.Time).ToList();
            if (startTimes.Count == 0)
            {
                return timing;
            }

            List<TrackLine> sectorLines = map.SectorLines.ToList();

            if (map.SameStartFinish)
            {
                // each start crossing opens a lap; the next one closes it, last stays open
                for (int i = 0; i < startTimes.Count; i++)
                {
                    LapTiming lap = new()
                    {
                        LapNumber = i + 1,
                        StartTime = startTimes[i],
                        EndTime = i + 1 < startTimes.Count ? startTimes[i + 1] : null,
                    };
                    AddSectors(lap, sectorLines, crossings);
                    timing.Laps.Add(lap);
                }
            }
            else
            {
                // point-to-point: first start, then the first finish after it
                float startTime = startTimes[0];
                float? endTime = crossings
                    .Where(c => c.Line.Type == LineType.Finish && c.Time > startTime)
                    .Select(c => (float?)c.Time)
                    .FirstOrDefault();
                LapTiming lap = new() { LapNumber = 1, StartTime = startTime, EndTime = endTime };
                AddSectors(lap, sectorLines, crossings);
                timing.Laps.Add(lap);
            }

            return timing;
        }

        /// <summary>First crossing of each sector line inside the lap's [start, end) window,
        /// recorded as a split from the lap start.</summary>
        private static void AddSectors(LapTiming lap, List<TrackLine> sectorLines, List<LineCrossing> crossings)
        {
            foreach (TrackLine sector in sectorLines)
            {
                foreach (LineCrossing c in crossings)
                {
                    if (!ReferenceEquals(c.Line, sector) || c.Time < lap.StartTime)
                    {
                        continue;
                    }
                    if (lap.EndTime.HasValue && c.Time >= lap.EndTime.Value)
                    {
                        break; // crossings are time-sorted - past the window, this sector's out
                    }
                    lap.Sectors.Add(new SectorSplit(sector.Order, c.Time, c.Time - lap.StartTime));
                    break; // first crossing in the window only
                }
            }
        }

        /// <summary>Joins the run's Latitude/Longitude channels on matching timestamps into a
        /// time-ordered track (same join the XY track-map chart uses).</summary>
        private static List<(float Time, double Lat, double Lon)> GetTrackPoints(RunData run)
        {
            List<(float, double, double)> points = new();
            if (!run.channels.TryGetValue("Latitude", out DataChannel? latChan) ||
                !run.channels.TryGetValue("Longitude", out DataChannel? lonChan))
            {
                return points;
            }
            foreach (KeyValuePair<float, float> lat in latChan.DataPoints)
            {
                if (lonChan.DataPoints.TryGetValue(lat.Key, out float lon))
                {
                    points.Add((lat.Key, lat.Value, lon));
                }
            }
            return points;
        }

        private static void AddCrossingsForLine(
            List<(float Time, double Lat, double Lon)> track, TrackLine line, TrackMapUnits units, List<LineCrossing> crossings)
        {
            double theta = line.Heading * Math.PI / 180.0;
            double travelEast = Math.Sin(theta);
            double travelNorth = Math.Cos(theta);
            double lineEast = Math.Cos(theta);
            double lineNorth = -Math.Sin(theta);
            double halfWidthMeters = ToMeters(line.Width, units) / 2.0;
            double cosLat = CosLat(line.Latitude);

            // line segment endpoints A/B in the line-centered east/north frame
            double ax = halfWidthMeters * lineEast, ay = halfWidthMeters * lineNorth;
            double sx = -2.0 * ax, sy = -2.0 * ay; // B - A

            // project a track point into the line-centered meters frame
            (double X, double Y) project((float Time, double Lat, double Lon) p) =>
                ((p.Lon - line.Longitude) * MetersPerDegreeLatitude * cosLat,
                 (p.Lat - line.Latitude) * MetersPerDegreeLatitude);

            (double px0, double py0) = project(track[0]);
            for (int i = 1; i < track.Count; i++)
            {
                (double px1, double py1) = project(track[i]);
                double rx = px1 - px0, ry = py1 - py0;

                // must be moving through the line in the travel direction
                double along = rx * travelEast + ry * travelNorth;
                if (along > 0)
                {
                    double denom = rx * sy - ry * sx; // r × s
                    if (Math.Abs(denom) > ParallelEpsilon)
                    {
                        double qx = ax - px0, qy = ay - py0; // A - P0
                        double t = (qx * sy - qy * sx) / denom; // fraction along the run segment
                        double u = (qx * ry - qy * rx) / denom; // fraction along the line segment
                        if (t >= 0.0 && t <= 1.0 && u >= 0.0 && u <= 1.0)
                        {
                            float t0 = track[i - 1].Time, t1 = track[i].Time;
                            float crossTime = (float)(t0 + t * (t1 - t0));
                            double crossLat = track[i - 1].Lat + t * (track[i].Lat - track[i - 1].Lat);
                            double crossLon = track[i - 1].Lon + t * (track[i].Lon - track[i - 1].Lon);
                            crossings.Add(new LineCrossing(line, crossTime, crossLat, crossLon));
                        }
                    }
                }

                px0 = px1;
                py0 = py1;
            }
        }

        private static double ToMeters(double value, TrackMapUnits units) =>
            units == TrackMapUnits.Feet ? value * FeetToMeters : value;

        private static double CosLat(double latDegrees)
        {
            // clamp so a garbage latitude can't collapse the longitude scaling to zero
            double c = Math.Cos(latDegrees * Math.PI / 180.0);
            return Math.Abs(c) < 0.05 ? 0.05 : c;
        }
    }
}
