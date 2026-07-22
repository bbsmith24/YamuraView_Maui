namespace YamuraView.Core
{
    /// <summary>
    /// Calculated "Delta-T" channel (like the WinForms app's delta time): pick a base run,
    /// and every run gets a channel of (this run's aligned time at distance d) minus (the
    /// base run's aligned time at the same distance) - the classic time-gained/lost-versus-
    /// reference trace. Positive = behind the base run at that point on track. The base run
    /// itself computes to ~0 everywhere, a flat reference line.
    /// </summary>
    public static class DeltaTime
    {
        public const string ChannelName = "Delta-T";

        // prefer the calculated distance channel; fall back to the GPS-derived one
        static readonly string[] DistanceChannelPreference = { "Distance", "Distance-GPS" };

        static DataChannel? DistanceChannelFor(RunData run)
        {
            foreach (string name in DistanceChannelPreference)
            {
                if (run.channels.TryGetValue(name, out DataChannel? channel) && channel.DataPoints.Count > 0)
                {
                    return channel;
                }
            }
            return null;
        }

        /// <summary>
        /// (Re)computes Delta-T on every run against the given base run, replacing any
        /// previous computation (e.g. with a different base or before a re-alignment).
        /// Alignment offsets are honored on both axes: distances are matched in
        /// DistanceOffset-aligned space and the delta uses TimeOffset-aligned times. Each
        /// run's trace is then rebased so it reads exactly zero at the defined start point -
        /// the alignment itself is only as exact as the GPS sample spacing, and that residue
        /// would otherwise show up as a constant offset instead of the trace starting at 0.
        /// Returns a warning listing runs that were skipped (no distance data), or null if
        /// every run got the channel.
        /// </summary>
        public static string? Compute(DataLogger dataLogger, string baseRunName)
        {
            RunData? baseRun = dataLogger.runData.FirstOrDefault(r => r.runName == baseRunName);
            DataChannel? baseDistance = baseRun == null ? null : DistanceChannelFor(baseRun);
            if (baseRun == null || baseDistance == null)
            {
                return $"Base run {baseRunName} has no distance data.";
            }

            // points before the distance align point are excluded everywhere: that's where
            // the runs were matched up, and anything earlier (out-lap, sitting on grid) has
            // no meaningful time comparison. NaN (never distance-aligned) disables the cut.
            float alignPoint = dataLogger.DistanceAlignPoint;
            bool hasAlignPoint = !float.IsNaN(alignPoint);

            // base curve: aligned distance -> aligned time, filtered to strictly increasing
            // distance so plateaus (car stationary) and GPS jitter don't break interpolation
            List<(float Dist, float Time)> baseCurve = new();
            float lastDist = float.MinValue;
            foreach (KeyValuePair<float, float> point in baseDistance.DataPoints)
            {
                float dist = point.Value + baseRun.DistanceOffset;
                if (hasAlignPoint && dist < alignPoint)
                {
                    continue;
                }
                if (dist > lastDist)
                {
                    baseCurve.Add((dist, point.Key + baseRun.TimeOffset));
                    lastDist = dist;
                }
            }
            if (baseCurve.Count < 2)
            {
                return $"Base run {baseRunName} has too little distance data.";
            }

            List<string> skipped = new();
            foreach (RunData run in dataLogger.runData)
            {
                // recompute from scratch - the channel may exist from an earlier base choice
                run.channels.Remove(ChannelName);
                run.channelRanges.Remove(ChannelName);

                DataChannel? distance = DistanceChannelFor(run);
                if (distance == null)
                {
                    skipped.Add(run.runName);
                    continue;
                }
                run.AddChannel(ChannelName, $"Time delta vs {baseRunName} at distance", "Calculated", run.runName, 1.0F);

                // raw deltas first (time order), then rebase on the run's delta at the start
                // point so every run's trace starts at exactly 0 there and reads "time
                // gained/lost since the start position"
                List<(float Time, float Delta)> deltas = new();
                foreach (KeyValuePair<float, float> point in distance.DataPoints)
                {
                    float alignedDist = point.Value + run.DistanceOffset;
                    if (hasAlignPoint && alignedDist < alignPoint)
                    {
                        continue; // before the align point - runs aren't comparable yet
                    }
                    float? baseTime = InterpolateTime(baseCurve, alignedDist);
                    if (!baseTime.HasValue)
                    {
                        continue; // outside the base run's distance coverage
                    }
                    deltas.Add((point.Key, point.Key + run.TimeOffset - baseTime.Value));
                }
                // without a defined start point there's nothing to rebase on - the raw
                // difference is all there is
                float deltaAtStart = hasAlignPoint && deltas.Count > 0 ? deltas[0].Delta : 0.0F;
                foreach ((float time, float delta) in deltas)
                {
                    run.AddChannelData(ChannelName, time, delta - deltaAtStart);
                }
            }
            return skipped.Count > 0
                ? "No distance data, Delta-T skipped for: " + string.Join(", ", skipped)
                : null;
        }

        /// <summary>Linear interpolation of the base run's aligned time at a given aligned
        /// distance; null outside the base run's distance coverage.</summary>
        static float? InterpolateTime(List<(float Dist, float Time)> curve, float dist)
        {
            if (dist < curve[0].Dist || dist > curve[^1].Dist)
            {
                return null;
            }
            int lo = 0, hi = curve.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (curve[mid].Dist < dist)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            // lo = first index with Dist >= dist
            if (curve[lo].Dist == dist || lo == 0)
            {
                return curve[lo].Time;
            }
            (float d0, float t0) = curve[lo - 1];
            (float d1, float t1) = curve[lo];
            float fraction = (dist - d0) / (d1 - d0); // d1 > d0: curve is strictly increasing
            return t0 + fraction * (t1 - t0);
        }
    }
}
