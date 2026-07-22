namespace YamuraView.Core
{
    /// <summary>
    /// run header - one per run, contains global data for run
    /// </summary>
    public class DataLogger
    {
        public float[] minMaxTimestamp = new float[] { float.MaxValue, float.MinValue };
        public float[] minMaxLong = new float[] { float.MaxValue, float.MinValue };
        public float[] minMaxLat = new float[] { float.MaxValue, float.MinValue };
        public float[] minMaxSpeed = new float[] { float.MaxValue, float.MinValue };

        public float[][] minMaxAccel = new float[][] {new float[] {float.MaxValue, float.MinValue},
                                                      new float[] {float.MaxValue, float.MinValue},
                                                      new float[] {float.MaxValue, float.MinValue}};

        public List<RunData> runData = new List<RunData>();
        public Dictionary<string, float[]> channelRanges = new Dictionary<string, float[]>();

        /// <summary>
        /// Aligned-space distance of the most recent distance alignment event (the auto GPS
        /// align's matched point, or the manual alignment wizard's reference mark); NaN until
        /// an alignment happens. Calculated channels that compare runs at distance (Delta-T)
        /// only start here - before this point the runs aren't comparable (pre-start data).
        /// </summary>
        public float DistanceAlignPoint { get; set; } = float.NaN;

        /// <summary>
        /// The user-defined start position, marked once on the first loaded run in the
        /// alignment wizard; null until defined. Runs loaded afterward are aligned
        /// automatically to it (see <see cref="RunAlignment"/>).
        /// </summary>
        public StartPosition? RunStartPosition { get; set; }

        public void UpdateChannelRange(string channelName, float curVal)
        {
            channelRanges[channelName][0] = curVal < channelRanges[channelName][0] ? curVal : channelRanges[channelName][0];
            channelRanges[channelName][1] = curVal > channelRanges[channelName][1] ? curVal : channelRanges[channelName][1];
        }
        public void Reset()
        {
            runData.Clear();
            channelRanges.Clear();
            DistanceAlignPoint = float.NaN;
            RunStartPosition = null;
            minMaxTimestamp = new float[] { float.MaxValue, float.MinValue };
            minMaxLong = new float[] { float.MaxValue, float.MinValue };
            minMaxLat = new float[] { float.MaxValue, float.MinValue };
            minMaxSpeed = new float[] { float.MaxValue, float.MinValue };

            minMaxAccel = new float[][] {new float[] {float.MaxValue, float.MinValue},
                                                          new float[] {float.MaxValue, float.MinValue},
                                                          new float[] {float.MaxValue, float.MinValue}};
        }
    }
    /// <summary>
    /// The user-defined start position: the GPS location marked on the first loaded run,
    /// plus the aligned-space (offsets applied) time and distance of that mark. A run is
    /// aligned by giving its GPS point nearest this location these same aligned values.
    /// </summary>
    public class StartPosition
    {
        /// <summary>GPS location of the mark; NaN if the reference run had no GPS data
        /// there (later runs then can't be auto-aligned).</summary>
        public float Latitude { get; set; } = float.NaN;
        public float Longitude { get; set; } = float.NaN;

        /// <summary>Aligned-space time of the mark - the value every run's matched point
        /// gets shifted onto.</summary>
        public float AlignedTime { get; set; } = float.NaN;

        /// <summary>Aligned-space distance of the mark; NaN if the reference run had no
        /// distance data (matched runs then keep their distance offset).</summary>
        public float AlignedDistance { get; set; } = float.NaN;
    }

    /// <summary>
    /// one per loaded run - channels, ranges, and per-run alignment offsets
    /// </summary>
    public class RunData
    {
        public Dictionary<string, float[]> channelRanges = new Dictionary<string, float[]>();
        public Dictionary<string, DataChannel> channels = new Dictionary<string, DataChannel>();
        public string dateStr = "";
        public string timeStr = "";
        public string fileName = "";
        public string runName = "";
        public float[] minMaxTimestamp = new float[] { float.MaxValue, float.MinValue };
        public float DistanceOffset { get; set; } = 0.0F;
        public float TimeOffset { get; set; } = 0.0F;
        public RunData(string run)
        {
            runName = run;
        }
        public void AddChannel(string name, string desc, string src, string run, float scl)
        {
            if (channels.ContainsKey(name))
            {
                return;
            }
            channelRanges.Add(name, new float[2] { float.MaxValue, float.MinValue });
            channels.Add(name, new DataChannel(name, desc, src, run, scl));
        }
        public void UpdateChannelRange(string channelName, float time, float curVal)
        {
            channelRanges[channelName][0] = curVal < channelRanges[channelName][0] ? curVal : channelRanges[channelName][0];
            channelRanges[channelName][1] = curVal > channelRanges[channelName][1] ? curVal : channelRanges[channelName][1];
        }
        public void AddChannelData(string channelName, float time, float value)
        {
            UpdateChannelRange(channelName, time, value);
            channels[channelName].DataPoints.Add(time, value);
        }
    }
    /// <summary>
    /// one logged channel's data points and metadata
    /// </summary>
    public class DataChannel
    {
        string channelName;
        string channelDescription;
        string channelSource;
        float channelScale;
        string runName;
        float[] xRange = new float[] { float.MaxValue, float.MinValue, 0.0F };
        float[] yRange = new float[] { float.MaxValue, float.MinValue, 0.0F };
        public SortedList<float, float> dataPoints = new SortedList<float, float>();
        public string ChannelName
        {
            get { return channelName; }
            set { channelName = value; }
        }
        public string ChannelDescription
        {
            get { return channelDescription; }
            set { channelDescription = value; }
        }
        public string ChannelSource
        {
            get { return channelSource; }
            set { channelSource = value; }
        }
        public float ChannelScale
        {
            get { return channelScale; }
            set { channelScale = value; }
        }
        public float[] XRange
        {
            get { return xRange; }
            set { xRange = value; }
        }
        public float[] YRange
        {
            get { return yRange; }
            set { yRange = value; }
        }
        public DataChannel(string name, string desc, string src, string run, float scale)
        {
            channelName = name;
            channelDescription = desc;
            channelSource = src;
            channelScale = scale;
            runName = run;
            dataPoints = new SortedList<float, float>();
        }
        public SortedList<float, float> DataPoints
        {
            get { return dataPoints; }
            set { dataPoints = value; }
        }
        public void AddPoint(float timeStamp, float value)
        {
            DataPoints[timeStamp] = value;
            xRange[0] = timeStamp < xRange[0] ? timeStamp : xRange[0];
            xRange[1] = timeStamp > xRange[1] ? timeStamp : xRange[1];
            xRange[2] = xRange[1] - xRange[0];
            yRange[0] = value < yRange[0] ? value : yRange[0];
            yRange[1] = value > yRange[1] ? value : yRange[1];
            yRange[2] = yRange[1] - yRange[0];
        }
    }
}
