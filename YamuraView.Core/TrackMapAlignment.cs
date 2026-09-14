namespace YamuraView.Core
{
    /// <summary>
    /// Aligns runs using a track map's start line instead of the nearest-point
    /// <see cref="StartPosition"/>. Where <see cref="RunAlignment"/> matches each run's GPS
    /// sample nearest a single marked point, this uses the run's precise, interpolated crossing
    /// of the start-line segment (see <see cref="TrackMapGeometry"/>) - a sub-sample anchor that
    /// isn't thrown off by which GPS sample happened to land closest. Every run is shifted so its
    /// start-line crossing sits at aligned time and distance zero, so the Strip Chart reads time
    /// relative to the start and the existing distance-based <see cref="DeltaTime"/> anchors
    /// exactly at the line. Pure Core: the app layer decides when to apply a map and displays the
    /// returned warnings.
    /// </summary>
    public static class TrackMapAlignment
    {
        /// <summary>Aligned time every run's start-line crossing is shifted onto.</summary>
        public const float AnchorTime = 0f;
        /// <summary>Aligned distance every run's start-line crossing is shifted onto - also the
        /// point <see cref="DataLogger.DistanceAlignPoint"/> gates Delta-T from.</summary>
        public const float AnchorDistance = 0f;

        /// <summary>
        /// Makes <paramref name="map"/> the active alignment source and aligns every loaded run
        /// to its start line. Sets <see cref="DataLogger.DistanceAlignPoint"/> to the start line
        /// and syncs <see cref="DataLogger.RunStartPosition"/> to the line's location. Returns a
        /// warning listing runs that never crossed the start line (left unaligned), or null if
        /// every run aligned. Runs with no start line in the map fail wholesale.
        /// </summary>
        public static string? ApplyToAllRuns(DataLogger dataLogger, TrackMap map)
        {
            TrackLine? startLine = map.StartLine;
            if (startLine == null)
            {
                return "Track map has no start line - can't align runs to it.";
            }

            dataLogger.AlignmentTrackMap = map;

            List<string> skipped = new();
            foreach (RunData run in dataLogger.runData)
            {
                if (AlignRun(run, map) != null)
                {
                    skipped.Add(run.runName);
                }
            }

            dataLogger.DistanceAlignPoint = AnchorDistance;
            dataLogger.RunStartPosition = new StartPosition
            {
                Latitude = (float)startLine.Latitude,
                Longitude = (float)startLine.Longitude,
                AlignedTime = AnchorTime,
                AlignedDistance = AnchorDistance,
            };

            return skipped.Count > 0
                ? "No start-line crossing, not aligned to track map: " + string.Join(", ", skipped)
                : null;
        }

        /// <summary>
        /// Aligns one run to the map's start line: sets its <see cref="RunData.TimeOffset"/> (and,
        /// where the run has distance data, <see cref="RunData.DistanceOffset"/>) so the first
        /// start-line crossing lands on the anchor time/distance. Returns a warning - and leaves
        /// the offsets unchanged - if the map has no start line or the run never crosses it. Use
        /// this for a run loaded after a map is already active; <see cref="ApplyToAllRuns"/>
        /// covers the initial apply.
        /// </summary>
        public static string? AlignRun(RunData run, TrackMap map)
        {
            if (map.StartLine == null)
            {
                return $"{run.runName}: track map has no start line.";
            }
            LineCrossing? start = TrackMapGeometry.FindCrossings(run, map)
                .FirstOrDefault(c => c.Line.Type == LineType.Start);
            if (start == null)
            {
                return $"{run.runName}: no start-line crossing, can't align to the track map.";
            }

            run.TimeOffset = AnchorTime - start.Time;
            float? rawDistance = DistanceAtTime(run, start.Time);
            if (rawDistance.HasValue)
            {
                run.DistanceOffset = AnchorDistance - rawDistance.Value;
            }
            return null;
        }

        /// <summary>
        /// The lap/sector timing of every loaded run against the map, for display (a splits/lap
        /// table). Returned per run in load order - runName isn't guaranteed unique, so this is a
        /// list, not a dictionary. A run that never crossed the start line has an empty
        /// <see cref="RunTiming.Laps"/>.
        /// </summary>
        public static List<(RunData Run, RunTiming Timing)> GetRunTimings(DataLogger dataLogger, TrackMap map) =>
            dataLogger.runData.Select(run => (run, TrackMapGeometry.BuildTiming(run, map))).ToList();

        /// <summary>Raw distance at <paramref name="rawTime"/>, linearly interpolated on the
        /// calculated Distance channel (falling back to Distance-GPS), or null if the run has no
        /// distance data. Interpolated rather than nearest-sample so it matches the sub-sample
        /// crossing time.</summary>
        private static float? DistanceAtTime(RunData run, float rawTime)
        {
            foreach (string name in new[] { "Distance", "Distance-GPS" })
            {
                if (run.channels.TryGetValue(name, out DataChannel? channel) && channel.DataPoints.Count > 0)
                {
                    return Interpolate(channel.DataPoints, rawTime);
                }
            }
            return null;
        }

        /// <summary>Linear interpolation of a time-keyed channel at <paramref name="time"/>,
        /// clamped to the channel's endpoints.</summary>
        private static float Interpolate(SortedList<float, float> points, float time)
        {
            IList<float> keys = points.Keys;
            IList<float> values = points.Values;
            if (time <= keys[0])
            {
                return values[0];
            }
            if (time >= keys[^1])
            {
                return values[^1];
            }
            // first index with key >= time
            int lo = 0, hi = keys.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] < time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            if (keys[lo] == time || lo == 0)
            {
                return values[lo];
            }
            float t0 = keys[lo - 1], t1 = keys[lo];
            float frac = (time - t0) / (t1 - t0);
            return values[lo - 1] + frac * (values[lo] - values[lo - 1]);
        }
    }
}
