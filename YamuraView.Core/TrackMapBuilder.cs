namespace YamuraView.Core
{
    /// <summary>
    /// Builds a <see cref="TrackMap"/> seed from an already-logged run's GPS - the walk trail
    /// comes from the run's Latitude/Longitude samples instead of a live track walk, so a map
    /// can be authored from a recorded session (e.g. a clean lap) with no GPS device present.
    /// The caller then places the start/finish/sector lines and notes and saves it.
    /// </summary>
    public static class TrackMapBuilder
    {
        /// <summary>True if the run carries GPS position data usable as a trail.</summary>
        public static bool HasGps(RunData run) =>
            run.channels.TryGetValue("Latitude", out DataChannel? lat) && lat.DataPoints.Count > 0 &&
            run.channels.ContainsKey("Longitude");

        /// <summary>
        /// A seed map whose <see cref="TrackMap.Walk"/> is the run's GPS track (points joined on
        /// matching timestamps, with speed/course carried over when present), named after the
        /// run. No lines or notes - the caller adds those. Returns an empty-trail map if the run
        /// has no GPS.
        /// </summary>
        public static TrackMap FromRun(RunData run, TrackMapUnits units = TrackMapUnits.Feet)
        {
            TrackMap map = new() { Name = run.runName, Units = units };
            if (!run.channels.TryGetValue("Latitude", out DataChannel? latChan) ||
                !run.channels.TryGetValue("Longitude", out DataChannel? lonChan))
            {
                return map;
            }
            run.channels.TryGetValue("Speed-GPS", out DataChannel? speedChan);
            run.channels.TryGetValue("Heading-GPS", out DataChannel? courseChan);

            foreach (KeyValuePair<float, float> lat in latChan.DataPoints)
            {
                if (!lonChan.DataPoints.TryGetValue(lat.Key, out float lon))
                {
                    continue;
                }
                map.Walk.Add(new TrackPoint
                {
                    Latitude = lat.Value,
                    Longitude = lon,
                    TimeSeconds = lat.Key,
                    Speed = speedChan != null && speedChan.DataPoints.TryGetValue(lat.Key, out float s) ? s : null,
                    Course = courseChan != null && courseChan.DataPoints.TryGetValue(lat.Key, out float c) ? c : null,
                });
            }
            return map;
        }
    }
}
