namespace YamuraView.Core
{
    /// <summary>
    /// Works out how a run's raw timestamps map to GPS UTC time, from the per-record GPS
    /// date/time fields: UTC (Unix seconds) = raw time + offset. The records only carry whole
    /// seconds, so the sub-second part comes from either the extended record's time of week
    /// (its millisecond part matches UTC's - GPS time differs from UTC by whole leap seconds)
    /// or, for basic records, the first record of each new second (the receiver's 10 Hz epochs
    /// sit on UTC 100 ms ticks, so that record is at about .0). The median of all candidates
    /// is used, so the odd glitched record or dropped epoch doesn't move it. The logger stamps
    /// a record when it arrives, a little after the GPS epoch, so the anchor carries that
    /// small constant latency - the video sync's nudge buttons absorb it.
    /// </summary>
    public class GpsUtcAnchorEstimator
    {
        private readonly List<double> candidates = new();
        private long lastWholeSecond = long.MinValue;
        private float lastRawTime = float.NaN;

        public void Add(float rawTime, int year, int month, int day, int hour, int minute, int second, uint? timeOfWeekMs)
        {
            if (year < 2000 || month < 1 || month > 12 || day < 1 || day > 31 ||
                hour > 23 || minute > 59 || second > 60)
            {
                return; // no date yet (receiver still acquiring) or a corrupt record
            }
            long wholeSecond;
            try
            {
                // a leap second (60) folds onto the next minute's :00 - close enough here
                wholeSecond = new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), TimeSpan.Zero)
                    .ToUnixTimeSeconds() + (second == 60 ? 1 : 0);
            }
            catch (ArgumentOutOfRangeException)
            {
                return; // e.g. day 31 of a 30-day month
            }

            if (timeOfWeekMs.HasValue)
            {
                // (double) first: long - float would be computed in float, losing whole seconds
                candidates.Add((double)wholeSecond + (timeOfWeekMs.Value % 1000) / 1000.0 - rawTime);
            }
            else if (lastWholeSecond != long.MinValue && wholeSecond == lastWholeSecond + 1 &&
                     rawTime - lastRawTime < 0.5f)
            {
                // first record of a new second, with the previous second's last record close
                // behind it - no gap that could hide where the second actually rolled over
                candidates.Add((double)wholeSecond - rawTime);
            }
            lastWholeSecond = wholeSecond;
            lastRawTime = rawTime;
        }

        /// <summary>The median offset, or NaN when no record gave a usable time.</summary>
        public double Estimate()
        {
            if (candidates.Count == 0)
            {
                return double.NaN;
            }
            List<double> sorted = candidates.OrderBy(c => c).ToList();
            return sorted[sorted.Count / 2];
        }
    }
}
