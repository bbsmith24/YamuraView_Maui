using System.Text;

namespace YamuraView.Core
{
    /// <summary>
    /// Parses YamuraLog TXT/YLG/YL5 log files into a DataLogger, and provides
    /// GPS-distance and run-alignment logic. UI-independent: callers are
    /// responsible for showing busy state, and for displaying any warnings
    /// returned from the Read*File methods and any messages sent to <see cref="Log"/>.
    /// </summary>
    public class LogFileParser
    {
        public bool DistanceAlign { get; set; } = true;
        public bool TimeAlign { get; set; } = true;
        public string TimeAlignChannel { get; set; } = "gX";
        public float TimeAlignThreshold { get; set; } = 0.5f;
        public bool TimeAlignRisingEdge { get; set; } = true;

        // caps accumulated parse-warning text: a badly mismatched file can otherwise
        // produce a warning line per record/sentence, ballooning memory during the parse
        // and then locking up the UI thread when the text hits the warnings dialog
        const int MaxErrorTextLength = 16_000;
        static void AppendParseError(StringBuilder errStr, string message)
        {
            if (errStr.Length >= MaxErrorTextLength)
            {
                return;
            }
            errStr.AppendLine(message);
            if (errStr.Length >= MaxErrorTextLength)
            {
                errStr.AppendLine("(further parse warnings suppressed)");
            }
        }

        /// <summary>
        /// Optional sink for non-fatal parse issues (mirrors the WinForms app's AppLogger).
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Reads a YamuraLog v6 TXT file into <paramref name="dataLogger"/>.
        /// Returns an empty string (this format has no recoverable-error reporting).
        /// </summary>
        public string ReadTXTFile(DataLogger dataLogger, string fileName)
        {
            string inputStr;
            int runIdx = 0;
            float priorLatVal = 0.0F;
            float priorLongVal = 0.0F;
            float latVal = 0.0F;
            float longVal = 0.0F;
            float gX = 0.0F;
            float gY = 0.0F;
            float gZ = 0.0F;
            ulong timestamp = 0;
            ulong timestampOffset = 0;
            float timestampSeconds = 0.0F;
            float mph = 0;
            float heading = 0;
            bool timestampOffsetValid = false;
            bool gpsDistanceValid = false;
            float gpsDist = 0.0F;

            string tempLogFile = fileName.Replace(".txt", ".tmp");
            tempLogFile = tempLogFile.Replace(".TXT", ".TMP");
            StreamReader readLog = new StreamReader(fileName, true);
            StreamWriter writeLog = new StreamWriter(tempLogFile, false);
            string tmp_text = readLog.ReadToEnd();
            StringBuilder gpx_text = new StringBuilder();
            foreach (char c in tmp_text)
            {
                if ((c != 0x01) && (c != 0x11) && (c != 0x0C))
                {
                    writeLog.Write(c);
                    gpx_text.Append(c);
                }
            }
            readLog.Close();
            writeLog.Close();

            string[] splitStr;
            string runName = GetFileName(fileName, false);

            StreamReader readTemp = new StreamReader(tempLogFile, true);
            while (!readTemp.EndOfStream)
            {
                inputStr = readTemp.ReadLine() ?? "";
                if (inputStr.Length == 0)
                {
                    continue;
                }
                // run start, add a new run to logger
                if (string.Compare(inputStr, "Start", true) == 0)
                {
                    gpsDistanceValid = false;
                    gpsDist = 0.0F;
                    timestampOffsetValid = false;
                    timestampOffset = 0;
                    dataLogger.runData.Add(new RunData(runName));
                    runIdx = dataLogger.runData.Count - 1;
                    dataLogger.runData[runIdx].fileName = Path.GetFullPath(fileName);
                    dataLogger.runData[runIdx].runName = runName;
                    dataLogger.runData[runIdx].AddChannel("Time", "Timestamp", "Internal", runName, 1.0F);
                    continue;
                }
                // run end, skip just a marker
                else if ((string.Compare(inputStr, "Stop", true) == 0) ||
                         inputStr.StartsWith("GPS") ||
                         inputStr.StartsWith("Accel") ||
                         inputStr.StartsWith("Team Yamura"))
                {
                    continue;
                }
                splitStr = inputStr.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                timestamp = (ulong)BitConverter.ToUInt32(BitConverter.GetBytes(Convert.ToInt32(splitStr[0])), 0);
                if (!timestampOffsetValid)
                {
                    timestampOffset = timestamp;
                    timestampOffsetValid = true;
                }
                timestamp -= timestampOffset;
                timestampSeconds = Convert.ToSingle(timestamp) / 1000000.0F;
                dataLogger.runData[runIdx].channels["Time"].AddPoint(timestampSeconds, timestampSeconds);

                // gps only (8 fields), or gps+accelerometer (11 fields)
                if ((splitStr.Length == 8) || (splitStr.Length == 11))
                {
                    latVal = Convert.ToSingle(splitStr[3]);
                    longVal = Convert.ToSingle(splitStr[4]);
                    mph = Convert.ToSingle(splitStr[5]);
                    heading = Convert.ToSingle(splitStr[6]);
                    if (dataLogger.runData[runIdx].dateStr.Length == 0)
                    {
                        dataLogger.runData[runIdx].dateStr = splitStr[1];
                        dataLogger.runData[runIdx].timeStr = splitStr[2];
                    }
                    dataLogger.runData[runIdx].AddChannel("Latitude", "GPS Latitude", "GPS", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("Longitude", "GPS Longitude", "GPS", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("Speed-GPS", "GPS Speed", "GPS", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("Heading-GPS", "GPS Heading", "GPS", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("Distance-GPS", "GPS Distance", "GPS", runName, 1.0F);
                    dataLogger.runData[runIdx].channels["Latitude"].AddPoint(timestampSeconds, latVal);
                    dataLogger.runData[runIdx].channels["Longitude"].AddPoint(timestampSeconds, longVal);
                    dataLogger.runData[runIdx].channels["Speed-GPS"].AddPoint(timestampSeconds, mph);
                    dataLogger.runData[runIdx].channels["Heading-GPS"].AddPoint(timestampSeconds, heading);
                    if (!gpsDistanceValid)
                    {
                        dataLogger.runData[runIdx].channels["Distance-GPS"].AddPoint(timestampSeconds, 0.0F);
                        priorLatVal = latVal;
                        priorLongVal = longVal;
                        gpsDistanceValid = true;
                    }
                    else
                    {
                        gpsDist += GPSDistance(priorLatVal, priorLongVal, latVal, longVal);
                        dataLogger.runData[runIdx].channels["Distance-GPS"].AddPoint(timestampSeconds, gpsDist);
                        priorLatVal = latVal;
                        priorLongVal = longVal;
                    }
                }
                // accelerometer only (4 fields), or gps+accelerometer (11 fields)
                if ((splitStr.Length == 4) || (splitStr.Length == 11))
                {
                    int xValIdx = splitStr.Length == 4 ? 1 : 8;
                    int yValIdx = splitStr.Length == 4 ? 2 : 9;
                    int zValIdx = splitStr.Length == 4 ? 3 : 10;

                    gX = Convert.ToSingle(splitStr[xValIdx]);
                    gY = Convert.ToSingle(splitStr[yValIdx]);
                    gZ = Convert.ToSingle(splitStr[zValIdx]);
                    dataLogger.runData[runIdx].AddChannel("gX", "X Axis Acceleration", "IMU", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("gY", "Y Axis Acceleration", "IMU", runName, 1.0F);
                    dataLogger.runData[runIdx].AddChannel("gZ", "Z Axis Acceleration", "IMU", runName, 1.0F);
                    dataLogger.runData[runIdx].channels["gX"].AddPoint(timestampSeconds, gX);
                    dataLogger.runData[runIdx].channels["gY"].AddPoint(timestampSeconds, gY);
                    dataLogger.runData[runIdx].channels["gZ"].AddPoint(timestampSeconds, gZ);
                }
            }
            readTemp.Close();
            File.Delete(tempLogFile);
            return "";
        }

        /// <summary>
        /// Reads a YamuraLog v6 YLG (binary, tagged-record) file into <paramref name="dataLogger"/>.
        /// Returns any non-fatal parse warnings collected while reading.
        /// </summary>
        public string ReadYLGFile(DataLogger dataLogger, string fileName)
        {
            char[] b = new char[3];
            int runIdx;
            uint timeStamp = 0;
            uint timeStampOffset = 0;
            bool timeStampOffsetSet = false;
            float timestampSeconds = 0;
            float priorLatVal = 0.0F;
            float priorLongVal = 0.0F;
            bool gpsDistanceValid = false;
            float gpsDist = 0.0F;
            StringBuilder errStr = new StringBuilder();

            string runName = GetFileName(fileName, false);
            dataLogger.runData.Add(new RunData(runName));
            runIdx = dataLogger.runData.Count - 1;

            dataLogger.runData[runIdx].AddChannel("Time", "Timestamp", "Internal", runName, 1.0F);
            dataLogger.runData[runIdx].fileName = Path.GetFullPath(fileName);

            using (BinaryReader inFile = new BinaryReader(File.Open(fileName, FileMode.Open)))
            {
                // check for EOF
                while (inFile.BaseStream.Position != inFile.BaseStream.Length)
                {
                    try
                    {
                        b[0] = (char)inFile.ReadByte();
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"ReadYLGFile: error reading record type byte from {fileName}: {ex.Message}");
                        continue;
                    }
                    // 'T', next 4 bytes are a unsigned long int
                    if ((char)b[0] == 'T')
                    {
                        timeStamp = inFile.ReadUInt32();
                        if (!timeStampOffsetSet)
                        {
                            timeStampOffset = timeStamp;
                            timeStampOffsetSet = true;
                        }
                        timeStamp -= timeStampOffset;
                        timestampSeconds = (float)timeStamp / 1000000.0F;
                        dataLogger.runData[runIdx].channels["Time"].AddPoint(timestampSeconds, timestampSeconds);
                        continue;
                    }
                    try
                    {
                        b[1] = (char)inFile.ReadByte();
                        b[2] = (char)inFile.ReadByte();
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"ReadYLGFile: error reading channel type from {fileName}: {ex.Message}");
                        break;
                    }
                    // GPS (gps device) returns NMEA strings - 4 byte channel number followed by NMEA string
                    if ((b[0] == 'G') && (b[1] == 'P') && (b[2] == 'S'))
                    {
                        inFile.ReadUInt32();
                        if (ParseGPS_NMEA(inFile, out _, out _, out _, out _, out float lat, out _, out float lng, out _, out float hd, out float speed, out _, ref errStr))
                        {
                            dataLogger.runData[runIdx].AddChannel("Latitude", "GPS Latitude", "GPS", runName, 1.0F);
                            dataLogger.runData[runIdx].AddChannel("Longitude", "GPS Longitude", "GPS", runName, 1.0F);
                            dataLogger.runData[runIdx].AddChannel("Speed-GPS", "GPS Speed", "GPS", runName, 1.0F);
                            dataLogger.runData[runIdx].AddChannel("Heading-GPS", "GPS Heading", "GPS", runName, 1.0F);
                            dataLogger.runData[runIdx].AddChannel("Distance-GPS", "GPS Distance", "GPS", runName, 1.0F);
                            dataLogger.runData[runIdx].channels["Latitude"].AddPoint(timestampSeconds, lat);
                            dataLogger.runData[runIdx].channels["Longitude"].AddPoint(timestampSeconds, lng);
                            dataLogger.runData[runIdx].channels["Speed-GPS"].AddPoint(timestampSeconds, speed);
                            dataLogger.runData[runIdx].channels["Heading-GPS"].AddPoint(timestampSeconds, hd);
                            if (!gpsDistanceValid)
                            {
                                dataLogger.runData[runIdx].channels["Distance-GPS"].AddPoint(timestampSeconds, 0.0F);
                                priorLatVal = lat;
                                priorLongVal = lng;
                                gpsDistanceValid = true;
                            }
                            else
                            {
                                gpsDist += GPSDistance(priorLatVal, priorLongVal, lat, lng);
                                dataLogger.runData[runIdx].channels["Distance-GPS"].AddPoint(timestampSeconds, gpsDist);
                            }
                        }
                    }
                    // ACC - 3 axis accelerometer, byte channel number followed by 3 float values
                    else if ((b[0] == 'A') && (b[1] == 'C') && (b[2] == 'C'))
                    {
                        inFile.ReadUInt32();
                        dataLogger.runData[runIdx].AddChannel("gX", "X Axis Acceleration", "IMU", runName, 1.0F);
                        dataLogger.runData[runIdx].AddChannel("gY", "Y Axis Acceleration", "IMU", runName, 1.0F);
                        dataLogger.runData[runIdx].AddChannel("gZ", "Z Axis Acceleration", "IMU", runName, 1.0F);
                        for (int valIdx = 0; valIdx < 3; valIdx++)
                        {
                            float accelVal = inFile.ReadSingle();
                            if (valIdx == 0)
                            {
                                dataLogger.runData[runIdx].channels["gX"].AddPoint(timestampSeconds, accelVal);
                            }
                            else if (valIdx == 1)
                            {
                                dataLogger.runData[runIdx].channels["gY"].AddPoint(timestampSeconds, accelVal);
                            }
                            else if (valIdx == 2)
                            {
                                dataLogger.runData[runIdx].channels["gZ"].AddPoint(timestampSeconds, accelVal);
                            }
                        }
                    }
                    // analog channel - 4 byte channel number followed by 1 float value
                    else if ((b[0] == 'A') && (b[1] == '2') && (b[2] == 'D'))
                    {
                        uint channelNum = inFile.ReadUInt32();
                        uint channelVal = inFile.ReadUInt32();
                        float channelValF = channelVal;
                        string channelName = "A2D" + channelNum.ToString();
                        dataLogger.runData[runIdx].AddChannel(channelName, "Analog to Digital channel " + channelName, "A2D", runName, 1.0F);
                        dataLogger.runData[runIdx].channels[channelName].AddPoint(timestampSeconds, channelValF);
                    }
                    else
                    {
                        AppendParseError(errStr, $"unexpected channel type - read {b[0]}{b[1]}");
                    }
                }
                inFile.Close();
            }

            return errStr.ToString();
        }

        /// <summary>
        /// Reads a YamuraLog v7 CAN (YL5) file into <paramref name="dataLogger"/>. Returns any
        /// non-fatal parse warnings collected while reading. (Automatic GPS/time alignment
        /// against the other loaded runs is disabled - see the Align Runs wizard.)
        /// </summary>
        public string ReadYL5File(DataLogger dataLogger, string fileName)
        {
            int runIdx;
            float priorLatVal = 0.0F;
            float priorLongVal = 0.0F;
            bool gpsDistanceValid = false;
            uint absTime_uint;
            float absTime = 0.0F;
            float offsetTime = -1.0F;
            float gpsDist = 0.0F;
            bool hasGPS = false;
            string channelName;
            List<float> timestamps = new List<float> { 0.0F };

            StringBuilder errStr = new StringBuilder();

            string runName = GetFileName(fileName, false);

            dataLogger.runData.Add(new RunData(runName));
            runIdx = dataLogger.runData.Count - 1;
            dataLogger.runData[runIdx].AddChannel("Time", "Timestamp", "Internal", runName, 1.0F);
            dataLogger.runData[runIdx].fileName = Path.GetFullPath(fileName);

            using (BinaryReader inFile = new BinaryReader(File.Open(fileName, FileMode.Open)))
            {
                while (true)
                {
                    try
                    {
                        byte recordType = inFile.ReadByte();
                        // AD node (0x30-0x3F)
                        if ((recordType >= 0x30) && (recordType <= 0x3F))
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);

                            byte digitalVals = inFile.ReadByte();
                            ushort[] a2d = new ushort[8];
                            for (int idx = 0; idx < 8; idx++)
                            {
                                channelName = "D_" + ((recordType - 0x30) + idx).ToString();
                                if (!dataLogger.runData[runIdx].channels.ContainsKey(channelName))
                                {
                                    dataLogger.runData[runIdx].AddChannel(channelName, "Digital channel " + channelName, "D", runName, 1.0F);
                                }
                                dataLogger.runData[runIdx].channels[channelName].AddPoint(absTime, (digitalVals >> idx) & 0x01);
                            }
                            for (int idx = 0; idx < 8; idx++)
                            {
                                a2d[idx] = inFile.ReadUInt16();
                                channelName = "A2D_" + ((recordType - 0x30) + idx).ToString();
                                if (!dataLogger.runData[runIdx].channels.ContainsKey(channelName))
                                {
                                    dataLogger.runData[runIdx].AddChannel(channelName, "Analog to Digital channel " + channelName, "A2D", runName, 1.0F);
                                }
                                dataLogger.runData[runIdx].channels[channelName].AddPoint(absTime, a2d[idx]);
                            }
                        }
                        // IMU/accelerometer node (0x40)
                        else if (recordType == 0x40)
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);

                            float ax = inFile.ReadSingle();
                            float ay = inFile.ReadSingle();
                            float az = inFile.ReadSingle();
                            if (!dataLogger.runData[runIdx].channels.ContainsKey("gX"))
                            {
                                dataLogger.runData[runIdx].AddChannel("gX", "Accelerometer channel gX", "IMU", runName, 1.0F);
                            }
                            if (!dataLogger.runData[runIdx].channels.ContainsKey("gY"))
                            {
                                dataLogger.runData[runIdx].AddChannel("gY", "Accelerometer channel gY", "IMU", runName, 1.0F);
                            }
                            if (!dataLogger.runData[runIdx].channels.ContainsKey("gZ"))
                            {
                                dataLogger.runData[runIdx].AddChannel("gZ", "Accelerometer channel gZ", "IMU", runName, 1.0F);
                            }
                            dataLogger.runData[runIdx].channels["gX"].AddPoint(absTime, ax);
                            dataLogger.runData[runIdx].channels["gY"].AddPoint(absTime, ay);
                            dataLogger.runData[runIdx].channels["gZ"].AddPoint(absTime, az);
                        }
                        // GPS node (0x50)
                        else if (recordType == 0x50)
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);

                            inFile.ReadUInt16(); // gps year
                            inFile.ReadByte(); // gps month
                            inFile.ReadByte(); // gps day
                            inFile.ReadByte(); // gps hour
                            inFile.ReadByte(); // gps minute
                            inFile.ReadByte(); // gps second
                            float latitude = inFile.ReadInt32() / 10000000.0F;
                            float longitude = inFile.ReadInt32() / 10000000.0F;
                            float course = inFile.ReadInt32();
                            float speed = (inFile.ReadInt32() / 1000.0F) * 2.23694F; // convert GPS meters/sec to MPH
                            byte SIV = inFile.ReadByte();
                            if (SIV > 0)
                            {
                                if (gpsDistanceValid)
                                {
                                    float gpsStepDist = GPSDistance(priorLatVal, priorLongVal, latitude, longitude);
                                    gpsDist += gpsStepDist;
                                }
                                priorLatVal = latitude;
                                priorLongVal = longitude;
                                gpsDistanceValid = true;
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Latitude"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Latitude", "GPS Latitude", "GPS", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Longitude"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Longitude", "GPS Longitude", "GPS", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Speed-GPS"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Speed-GPS", "GPS Speed", "GPS", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Heading-GPS"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Heading-GPS", "GPS Heading", "GPS", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Distance-GPS"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Distance-GPS", "GPS Distance", "GPS", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("xDistance"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("xDistance", "Distance-Time", "Calculated", runName, 1.0F);
                                }
                                if (!dataLogger.runData[runIdx].channels.ContainsKey("Distance"))
                                {
                                    dataLogger.runData[runIdx].AddChannel("Distance", "Time-Distance", "Calculated", runName, 1.0F);
                                }
                                dataLogger.runData[runIdx].channels["Latitude"].AddPoint(absTime, latitude);
                                dataLogger.runData[runIdx].channels["Longitude"].AddPoint(absTime, longitude);
                                dataLogger.runData[runIdx].channels["Speed-GPS"].AddPoint(absTime, speed);
                                dataLogger.runData[runIdx].channels["Heading-GPS"].AddPoint(absTime, course);
                                dataLogger.runData[runIdx].channels["Distance-GPS"].AddPoint(absTime, gpsDist);
                                hasGPS = true;
                            }
                        }
                        // IR Tire temp node (0x60-0x6F)
                        else if ((recordType >= 0x60) && (recordType <= 0x6F))
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);
                        }
                        // Shock travel (0x70-0x7F)
                        else if ((recordType >= 0x70) && (recordType <= 0x7F))
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);
                        }
                        // Wheel Speed node (4 groups - 0x80-0x83; 0x84-0x87; 0x88-0x8B; 0x8C-0x8F)
                        else if ((recordType >= 0x80) && (recordType <= 0x8F))
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            // conversion for 205/50R15 tires (874.18 revs/mile) with 4 magnets
                            float interval = inFile.ReadUInt32();
                            interval = (4118.139976F / 4.0F) / interval;
                            if (!float.IsInfinity(interval) && !float.IsNaN(interval))
                            {
                                channelName = "SPD_" + (recordType - 0x80).ToString();
                                if (!dataLogger.runData[runIdx].channels.ContainsKey(channelName))
                                {
                                    dataLogger.runData[runIdx].AddChannel(channelName, "Wheelspeed channel " + channelName, "SPD", runName, 1.0F);
                                }
                                dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);
                                dataLogger.runData[runIdx].channels[channelName].AddPoint(absTime, interval);
                            }
                        }
                        // Engine RPM (0x90)
                        else if (recordType == 0x90)
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);
                        }
                        // CAN interface (0xA0)
                        else if (recordType == 0xA0)
                        {
                            absTime_uint = inFile.ReadUInt32();
                            absTime = absTime_uint / 1000.0F;
                            offsetTime = offsetTime < 0.0F ? absTime : offsetTime;
                            absTime -= offsetTime;
                            dataLogger.runData[runIdx].channels["Time"].AddPoint(absTime, absTime);
                        }
                        // unknown message - ignored
                        if (!timestamps.Contains(absTime))
                        {
                            timestamps.Add(absTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex is EndOfStreamException)
                        {
                            Log?.Invoke($"ReadYL5File: read log file {fileName}");
                        }
                        else
                        {
                            Log?.Invoke($"ReadYL5File: error reading record from {fileName}: {ex.Message}");
                        }
                        break;
                    }
                }
            }

            // interpolate for all times between GPS distance points - actual GPS distance points at
            // 10Hz are sparse compared to sensor data
            if (hasGPS && (timestamps.Count > 1))
            {
                // O(1)-indexed views of the GPS distance points - ElementAt on the
                // SortedList itself walks its enumerator O(index) deep on every call,
                // which made this whole block O(n^2) per file
                IList<float> gpsTimes = dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints.Keys;
                IList<float> gpsDists = dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints.Values;
                int[] gpsDistIdx = new int[2] { -1, 0 };
                float[] distRange = new float[3] { 0.0F, 0.0F, 0.0F };
                float[] timeRange = new float[3] { 0.0F, 0.0F, 0.0F };
                float interpolateDist;
                timeRange[0] = 0.0F;
                timeRange[1] = gpsTimes[0];
                timeRange[2] = timeRange[1] - timeRange[0];
                distRange[0] = 0.0F;
                distRange[1] = gpsDists[0];
                distRange[2] = distRange[1] - distRange[0];
                // first distance/time point is 0, 0
                dataLogger.runData[runIdx].channels["xDistance"].AddPoint(0.0F, 0.0F);
                dataLogger.runData[runIdx].channels["Distance"].AddPoint(0.0F, 0.0F);
                for (int timestampIdx = 0; timestampIdx < timestamps.Count; timestampIdx++)
                {
                    // data before first GPS point
                    if (timestamps[timestampIdx] < gpsTimes[0])
                    {
                        if (!dataLogger.runData[runIdx].channels["xDistance"].dataPoints.ContainsKey(0.0F))
                        {
                            dataLogger.runData[runIdx].channels["xDistance"].AddPoint(0.0F, timestamps[timestampIdx]);
                        }
                        dataLogger.runData[runIdx].channels["Distance"].AddPoint(timestamps[timestampIdx], 0.0F);
                        continue;
                    }
                    // add known GPS point, no interpolation required - update data range for interpolation
                    else if (dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints.ContainsKey(timestamps[timestampIdx]))
                    {
                        if (!dataLogger.runData[runIdx].channels["xDistance"].dataPoints.ContainsKey(dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints[timestamps[timestampIdx]]))
                        {
                            dataLogger.runData[runIdx].channels["xDistance"].AddPoint(
                                                                 dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints[timestamps[timestampIdx]],
                                                                 timestamps[timestampIdx]);
                        }
                        dataLogger.runData[runIdx].channels["Distance"].AddPoint(
                                                             timestamps[timestampIdx],
                                                             dataLogger.runData[runIdx].channels["Distance-GPS"].dataPoints[timestamps[timestampIdx]]);
                        // reset gps point range for interpolation
                        gpsDistIdx[0]++;
                        if (gpsDistIdx[1] < gpsTimes.Count - 1)
                        {
                            gpsDistIdx[1]++;
                        }
                        timeRange[0] = gpsTimes[gpsDistIdx[0]];
                        timeRange[1] = gpsTimes[gpsDistIdx[1]];
                        timeRange[2] = timeRange[1] - timeRange[0];
                        distRange[0] = gpsDists[gpsDistIdx[0]];
                        distRange[1] = gpsDists[gpsDistIdx[1]];
                        distRange[2] = distRange[1] - distRange[0];
                        continue;
                    }
                    // data after end of GPS data, add last known distance
                    else if (gpsDistIdx[0] == gpsDistIdx[1])
                    {
                        if (!dataLogger.runData[runIdx].channels["xDistance"].dataPoints.ContainsKey(gpsDists[gpsDistIdx[0]]))
                        {
                            dataLogger.runData[runIdx].channels["xDistance"].AddPoint(
                                 gpsDists[gpsDistIdx[0]],
                                 gpsTimes[gpsDistIdx[0]]);
                        }
                        dataLogger.runData[runIdx].channels["Distance"].AddPoint(
                             gpsTimes[gpsDistIdx[0]],
                             gpsDists[gpsDistIdx[0]]);
                        continue;
                    }
                    // time between 2 known distances - interpolate to get distance at time
                    interpolateDist = distRange[0] + (distRange[2] * ((timestamps[timestampIdx] - timeRange[0]) / timeRange[2]));
                    if (!dataLogger.runData[runIdx].channels["xDistance"].dataPoints.ContainsKey(interpolateDist))
                    {
                        dataLogger.runData[runIdx].channels["xDistance"].AddPoint(interpolateDist, timestamps[timestampIdx]);
                    }
                    dataLogger.runData[runIdx].channels["Distance"].AddPoint(timestamps[timestampIdx], interpolateDist);
                }
            }

            dataLogger.runData[runIdx].AddChannel("DeltaTime", "DeltaTime", "Calculated", dataLogger.runData[runIdx].runName, 1.0F);

            // automatic alignment disabled - runs are aligned through the Align Runs wizard
            // after each load (see MainPage.AlignNewRunsAsync). AlignGPS/AlignTime kept for now.
            //AlignGPS(dataLogger);
            //AlignTime(dataLogger);

            return errStr.ToString();
        }

        /// <summary>
        /// very specific NMEA parser for the output from Sparkfun QWIIC GPS breakout - see the
        /// Titan datasheet for more info. Handles GGA/RMC/VTG; GSA/GSV are recognized and skipped.
        /// </summary>
        public bool ParseGPS_NMEA(BinaryReader inFile, out string date, out int hr, out int min, out float sec, out float lat, out string ns, out float lng, out string ew, out float hd, out float speed, out int sat, ref StringBuilder errStr)
        {
            bool rVal;
            int utcHour = -1;
            int utcMin = -1;
            int utcSec = -1;
            int utcmSec = -1;
            int latDeg = -1;
            int latMin = -1;
            int latMinDecimal = -1;
            int longDeg = -1;
            int longMin = -1;
            int longMinDecimal = -1;
            int satellites = -1;
            float speedKnotsPH = 0.0F;
            float speedKmPH = 0.0F;
            float heading = 0.0F;
            string dateStr = "";
            string nsIndication = "";
            string ewIndication = "";
            lat = 0.0F;
            ns = "X";
            lng = 0.0F;
            ew = "X";
            hd = 0.0F;
            speed = 0.0F;
            sat = 0;
            date = "xx/xx/xxxx";
            hr = 0;
            min = 0;
            sec = 0F;

            char c;
            string dataSentence;
            // sentence always begins with '$', ends with 0x0D - except when it doesn't, since
            // sometimes the '$' gets dropped
            while ((inFile.PeekChar() == '$') || (inFile.PeekChar() == 'G'))
            {
                dataSentence = "";
                c = inFile.ReadChar();
                while (c != 0x0D)
                {
                    dataSentence += c;
                    c = (char)inFile.ReadByte();
                }
                // malformed, no '*'
                if (dataSentence.IndexOf('*') < 0)
                {
                    AppendParseError(errStr, $"malformed NMEA sentance - missing '*' {dataSentence}");
                    continue;
                }
                int receivedChecksum;
                // check for malformed, illegal char in hex value
                try
                {
                    receivedChecksum = Convert.ToInt32(dataSentence.Substring(dataSentence.IndexOf('*') + 1), 16);
                }
                catch (Exception ex)
                {
                    AppendParseError(errStr, $"error reading checksum from NMEA sentance {dataSentence}: {ex.Message}");
                    continue;
                }
                // calculate checksum for characters between $ and *
                int calculatedChecksum = 0;
                int charIdx = 1;
                while (dataSentence[charIdx] != '*')
                {
                    calculatedChecksum ^= Convert.ToByte(dataSentence[charIdx]);
                    charIdx++;
                }
                // bad checksum - skip this sentence
                if (calculatedChecksum != receivedChecksum)
                {
                    AppendParseError(errStr, $"checksum mismatch read 0x{receivedChecksum:X} calculated 0x{calculatedChecksum:X} for NMEA sentance {dataSentence}");
                    continue;
                }

                string[] words = dataSentence.Split(new char[] { ',' });
                try
                {
                    // GGA - Time, position and fix type data.
                    if (dataSentence.StartsWith("$GPGGA") || dataSentence.StartsWith("$GNGGA"))
                    {
                        utcHour = Convert.ToInt32(words[1].Substring(0, 2));
                        utcMin = Convert.ToInt32(words[1].Substring(2, 2));
                        utcSec = Convert.ToInt32(words[1].Substring(4, 2));
                        utcmSec = Convert.ToInt32(words[1].Substring(7, 3));

                        latDeg = Convert.ToInt32(words[2].Substring(0, 2));
                        latMin = Convert.ToInt32(words[2].Substring(2, 2));
                        latMinDecimal = Convert.ToInt32(words[2].Substring(5, 4));

                        nsIndication = words[3];

                        longDeg = Convert.ToInt32(words[4].Substring(0, 3));
                        longMin = Convert.ToInt32(words[4].Substring(3, 2));
                        longMinDecimal = Convert.ToInt32(words[4].Substring(6, 4));

                        ewIndication = words[5];

                        satellites = Convert.ToInt32(words[7]);
                    }
                    // RMC - Time, date, position, course and speed data.
                    else if (dataSentence.StartsWith("$GPRMC") || dataSentence.StartsWith("$GNRMC"))
                    {
                        utcHour = Convert.ToInt32(words[1].Substring(0, 2));
                        utcMin = Convert.ToInt32(words[1].Substring(2, 2));
                        utcSec = Convert.ToInt32(words[1].Substring(4, 2));
                        utcmSec = Convert.ToInt32(words[1].Substring(7, 3));

                        latDeg = Convert.ToInt32(words[3].Substring(0, 2));
                        latMin = Convert.ToInt32(words[3].Substring(2, 2));
                        latMinDecimal = Convert.ToInt32(words[3].Substring(5, 4));

                        nsIndication = words[4];

                        longDeg = Convert.ToInt32(words[5].Substring(0, 3));
                        longMin = Convert.ToInt32(words[5].Substring(3, 2));
                        longMinDecimal = Convert.ToInt32(words[5].Substring(6, 4));

                        ewIndication = words[6];

                        speedKnotsPH = Convert.ToSingle(words[7]);
                        heading = Convert.ToSingle(words[8]);
                        dateStr = words[9];
                    }
                    // VTG - Course and speed information relative to the ground.
                    else if (dataSentence.StartsWith("$GPVTG") || dataSentence.StartsWith("$GNVTG"))
                    {
                        heading = Convert.ToSingle(words[1]);
                        speedKnotsPH = Convert.ToSingle(words[5]);
                        speedKmPH = Convert.ToSingle(words[7]);
                    }
                    // GSA/GSV - recognized, intentionally skipped
                    else if (dataSentence.StartsWith("$GPGSA") || dataSentence.StartsWith("$GLGSA") ||
                             dataSentence.StartsWith("$GPGSV") || dataSentence.StartsWith("$GLGSV"))
                    {
                    }
                    else
                    {
                        AppendParseError(errStr, "ignored unknown/deformed NMEA sentance");
                    }
                }
                catch (Exception e)
                {
                    AppendParseError(errStr, $"ParseNMEA error reading sentence from {dataSentence} error: {e.Message}");
                }
            }
            if (latDeg == -1)
            {
                rVal = false;
            }
            else
            {
                rVal = true;
                if (dateStr.Length < 6)
                {
                    AppendParseError(errStr, $"ParseNMEA bad date string {dateStr}");
                    date = "xx/xx/xxxx";
                }
                else
                {
                    date = dateStr.Substring(2, 2) + "/" + dateStr.Substring(0, 2) + "/20" + dateStr.Substring(4, 2);
                }
                hr = utcHour;
                min = utcMin;
                sec = utcSec + utcmSec / 1000.0F;

                lat = latDeg + (latMin + (latMinDecimal / 10000.0F)) / 60.0F;
                ns = nsIndication;
                lng = longDeg + (longMin + (longMinDecimal / 10000.0F)) / 60.0F;
                ew = ewIndication;
                hd = heading;
                if ((speedKmPH == -1.0F) && (speedKnotsPH != -1.0F))
                {
                    speedKmPH = speedKnotsPH * 1.852F;
                }
                speed = speedKmPH;
                sat = satellites;
            }
            return rVal;
        }

        public string GetFileName(string filePath, bool includeExtension)
        {
            string fileName = Path.GetFileName(filePath);
            if (!includeExtension)
            {
                fileName = Path.GetFileNameWithoutExtension(filePath);
            }
            return fileName;
        }

        /// <summary>
        /// Aligns the most recently added run's distance offset to the first run using GPS data:
        /// finds the closest-matching lat/long point between the two runs and offsets distance
        /// so both runs read the same distance at that point.
        /// </summary>
        public void AlignGPS(DataLogger dataLogger)
        {
            if ((dataLogger.runData.Count() <= 1) || !DistanceAlign)
            {
                return;
            }
            float minDistanceBetweenPositions = float.MaxValue;
            float distanceOffset = 0.0F;
            float alignDistance = 0.0F;
            float distance1;
            float distance2 = 0.0F;
            int lastRunIdx = dataLogger.runData.Count - 1;
            bool distanceOffsetSet = false;
            int gpsIndexErrorCount = 0;
            // GPS points from first data set
            for (int gps1Idx = 0; gps1Idx < dataLogger.runData[0].channels["Latitude"].DataPoints.Count; gps1Idx++)
            {
                float gpsLat1 = dataLogger.runData[0].channels["Latitude"].DataPoints.ElementAt(gps1Idx).Value;
                float gpsLong1 = dataLogger.runData[0].channels["Longitude"].DataPoints.ElementAt(gps1Idx).Value;
                distance1 = dataLogger.runData[0].channels["Distance-GPS"].DataPoints.ElementAt(gps1Idx).Value;
                // GPS points from last added data set
                for (int gps2Idx = 0; gps2Idx < dataLogger.runData[lastRunIdx].channels["Latitude"].DataPoints.Count; gps2Idx++)
                {
                    float gpsLat2, gpsLong2;
                    try
                    {
                        gpsLat2 = dataLogger.runData[lastRunIdx].channels["Latitude"].DataPoints.ElementAt(gps2Idx).Value;
                        gpsLong2 = dataLogger.runData[lastRunIdx].channels["Longitude"].DataPoints.ElementAt(gps2Idx).Value;
                        distance2 = dataLogger.runData[lastRunIdx].channels["Distance-GPS"].DataPoints.ElementAt(gps2Idx).Value;
                    }
                    catch
                    {
                        // logged once, in aggregate, below the loop - this runs in a tight O(n^2) loop
                        gpsIndexErrorCount++;
                        continue;
                    }
                    float distanceBetweenPositions = GPSDistance(gpsLat1, gpsLong1, gpsLat2, gpsLong2);
                    if (distanceBetweenPositions < minDistanceBetweenPositions)
                    {
                        minDistanceBetweenPositions = distanceBetweenPositions;
                        if (minDistanceBetweenPositions == 0.0F)
                        {
                            if (!distanceOffsetSet)
                            {
                                distanceOffset = distance1 - distance2;
                                alignDistance = distance1;
                                distanceOffsetSet = true;
                            }
                            break;
                        }
                    }
                }
                if (distanceOffsetSet)
                {
                    break;
                }
            }
            dataLogger.runData[lastRunIdx].DistanceOffset = distanceOffset;
            if (distanceOffsetSet)
            {
                // remember where (in aligned distance space) the runs were matched up -
                // Delta-T only computes from this point on
                dataLogger.DistanceAlignPoint = alignDistance + dataLogger.runData[0].DistanceOffset;
            }
            if (gpsIndexErrorCount > 0)
            {
                Log?.Invoke($"AlignGPS: skipped {gpsIndexErrorCount} mismatched GPS data point index(es).");
            }
        }

        /// <summary>
        /// Aligns the most recently added run's time offset to the first run: the first
        /// threshold-crossing on <see cref="TimeAlignChannel"/> is assumed to be the start of the run.
        /// GPS data can't align time since there may be holds after the first matching location point.
        /// </summary>
        public void AlignTime(DataLogger dataLogger)
        {
            if (!TimeAlign || dataLogger.runData.Count == 0)
            {
                return;
            }

            // find threshold-crossing time for each run
            List<float> crossingTimes = new List<float>();
            foreach (RunData dataSet in dataLogger.runData)
            {
                float crossing = float.NaN;
                if (!dataSet.channels.ContainsKey(TimeAlignChannel))
                {
                    crossingTimes.Add(float.NaN);
                    continue;
                }
                float prev = float.NaN;
                foreach (KeyValuePair<float, float> pt in dataSet.channels[TimeAlignChannel].DataPoints)
                {
                    float val = pt.Value;
                    if (!float.IsNaN(prev))
                    {
                        bool crossed = TimeAlignRisingEdge
                            ? (prev < TimeAlignThreshold && val >= TimeAlignThreshold)
                            : (prev > TimeAlignThreshold && val <= TimeAlignThreshold);
                        if (crossed)
                        {
                            crossing = pt.Key;
                            break;
                        }
                    }
                    prev = val;
                }
                crossingTimes.Add(crossing);
            }

            // use run 0's crossing as reference; skip runs where channel is missing or never crossed
            float reference = crossingTimes[0];
            if (float.IsNaN(reference))
            {
                return;
            }

            for (int i = 0; i < dataLogger.runData.Count; i++)
            {
                if (!float.IsNaN(crossingTimes[i]))
                {
                    dataLogger.runData[i].TimeOffset = reference - crossingTimes[i];
                }
            }
        }

        /// <summary>
        /// great circle (haversine) distance between 2 lat/long points, in feet.
        /// </summary>
        public float GPSDistance(float lat1Deg, float long1Deg, float lat2Deg, float long2Deg)
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

        private float DegreesToRadians(double deg)
        {
            double rad = (deg * Math.PI) / 180.0;
            return (float)rad;
        }
    }
}
