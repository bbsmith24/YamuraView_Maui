namespace YamuraView.Core
{
    /// <summary>
    /// What a <see cref="TrackLine"/> marks. Start and Finish delimit a run; Sector lines are
    /// intermediate splits between them (or within a lap). On a circuit the same physical line
    /// is both start and finish - see <see cref="TrackMap.SameStartFinish"/> - in which case
    /// the map carries a single <see cref="Start"/> line and no <see cref="Finish"/>.
    /// </summary>
    public enum LineType
    {
        Start,
        Sector,
        Finish,
    }

    /// <summary>
    /// One GPS fix recorded during a track walk. Latitude/Longitude and the walk-relative
    /// timestamp are always present; the rest mirror what the device's location service
    /// supplied and are null when it didn't report them. The full ordered list of these
    /// (see <see cref="TrackMap.Walk"/>) is what lets an accurate track outline be drawn on
    /// any device, GPS-equipped or not.
    /// </summary>
    public sealed class TrackPoint
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        /// <summary>Seconds since the start of the walk recording.</summary>
        public float TimeSeconds { get; set; }
        /// <summary>Ground speed, if reported (device units, typically m/s).</summary>
        public float? Speed { get; set; }
        /// <summary>Course/heading over ground in degrees (0 = north), if reported.</summary>
        public float? Course { get; set; }
        /// <summary>Horizontal accuracy in meters, if reported.</summary>
        public float? AccuracyMeters { get; set; }
        /// <summary>Altitude in meters, if reported.</summary>
        public float? Altitude { get; set; }
    }

    /// <summary>
    /// A timing line on the track: a segment centered on a GPS point, perpendicular to the
    /// direction of travel through it. <see cref="Heading"/> is the travel bearing (compass
    /// degrees, 0 = north, clockwise) - so heading 0 draws a line running east/west that a car
    /// crosses heading north. <see cref="Width"/> is the full extent of the segment (the track
    /// width the line spans), centered on the point (±Width/2 either side). A run "crosses" the
    /// line when its GPS track passes through that segment in the travel direction; the crossing
    /// time is what drives start alignment and split/lap timing.
    /// </summary>
    public sealed class TrackLine
    {
        public LineType Type { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        /// <summary>Travel bearing through the line, degrees (0 = north, clockwise). The line
        /// segment itself is drawn perpendicular to this.</summary>
        public float Heading { get; set; }
        /// <summary>Full extent of the line in <see cref="TrackMap.Units"/>, centered on the
        /// point.</summary>
        public float Width { get; set; }
        /// <summary>Ordering among sector lines (1-based); ignored for Start/Finish.</summary>
        public int Order { get; set; }
    }

    /// <summary>
    /// A free-text annotation pinned to a GPS position (e.g. "optional slalom, enter on right
    /// side"). Notes are advisory only - they take no part in crossing detection or timing;
    /// they render as pins on the track map.
    /// </summary>
    public sealed class TrackNote
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// Distance unit a track map's line widths are expressed in.
    /// </summary>
    public enum TrackMapUnits
    {
        Feet,
        Meters,
    }

    /// <summary>
    /// A track map authored during a track walk and saved to a ".ytm" file (see
    /// <see cref="TrackMapFile"/>): the full GPS breadcrumb trail of the walk, the timing lines
    /// (start/finish/sectors), and any position notes. Authored on a GPS-equipped device, it is
    /// imported for analysis on any device - including ones with no GPS receiver - because the
    /// crossing/timing math (<see cref="TrackMapGeometry"/>) runs against the GPS channels
    /// already present in a loaded run.
    /// </summary>
    public sealed class TrackMap
    {
        public string Name { get; set; } = "";
        /// <summary>When the map was created (round-tripped as an ISO-8601 string); null if unknown.</summary>
        public DateTime? Created { get; set; }
        public TrackMapUnits Units { get; set; } = TrackMapUnits.Feet;

        /// <summary>
        /// Circuit vs. point-to-point. When true, the same physical line is both start and
        /// finish: the map carries a single Start line, and every crossing of it ends a lap.
        /// When false, Start and Finish are distinct lines delimiting a single pass, with any
        /// sectors as splits between them.
        /// </summary>
        public bool SameStartFinish { get; set; }

        /// <summary>Every GPS fix recorded during the walk, in time order - drawn as the track
        /// outline. May be empty (a map can be authored with lines only).</summary>
        public List<TrackPoint> Walk { get; set; } = new();

        /// <summary>The timing lines. Exactly one Start; a Finish only when
        /// <see cref="SameStartFinish"/> is false; zero or more Sectors.</summary>
        public List<TrackLine> Lines { get; set; } = new();

        public List<TrackNote> Notes { get; set; } = new();

        /// <summary>The single Start line, or null if none has been placed yet.</summary>
        public TrackLine? StartLine => Lines.FirstOrDefault(l => l.Type == LineType.Start);

        /// <summary>The Finish line, or null (point-to-point maps have one; circuit maps don't).</summary>
        public TrackLine? FinishLine => Lines.FirstOrDefault(l => l.Type == LineType.Finish);

        /// <summary>Sector lines in <see cref="TrackLine.Order"/> order.</summary>
        public IEnumerable<TrackLine> SectorLines =>
            Lines.Where(l => l.Type == LineType.Sector).OrderBy(l => l.Order);
    }
}
