using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace YamuraView.Core
{
    /// <summary>Result of reading a video's GPS clock: the UTC time of the video's first frame.</summary>
    /// <param name="UtcAtVideoStart">Unix seconds at video position 0, or null if not found.</param>
    /// <param name="SampleCount">How many GPS timestamps the estimate is the median of.</param>
    /// <param name="Message">Why it failed, or a short description of what was used.</param>
    /// <param name="DurationSeconds">The movie's length from its header; 0 if unknown.</param>
    public record VideoGpsTime(double? UtcAtVideoStart, int SampleCount, string Message, double DurationSeconds = 0);

    /// <summary>
    /// Reads the GPS clock out of a GoPro MP4's GPMF telemetry track, to place the video on a
    /// log's GPS UTC timeline (see <see cref="RunData.GpsUtcOffsetSeconds"/>). Only the MP4
    /// index and the small telemetry samples are read, never the video data, so it's quick
    /// even on multi-GB files.
    /// <para>
    /// MP4 side: find the track whose sample description is 'gpmd', then turn its sample
    /// table (stts durations, stsz sizes, stsc/stco chunk layout) into each telemetry sample's
    /// file offset and media start time. GPMF side: each sample is a KLV tree (DEVC > STRM >
    /// ...). Older cameras give one GPSU per sample (UTC of the payload, "yymmddhhmmss.sss")
    /// next to GPS5 and a GPSF fix flag; HERO11 and later give GPS9, whose every sample carries
    /// days since 2000 and seconds since midnight. Each timestamp minus its media time is a
    /// candidate for the UTC at video start, and the median is returned.
    /// </para>
    /// </summary>
    public static class GoProGpmf
    {
        public static VideoGpsTime ReadVideoStartUtc(string path)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ReadVideoStartUtc(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
            {
                return new VideoGpsTime(null, 0, $"Couldn't read the video: {ex.Message}");
            }
        }

        public static VideoGpsTime ReadVideoStartUtc(Stream stream)
        {
            Box? moov = FindChild(stream, 0, stream.Length, "moov");
            if (moov == null)
            {
                return new VideoGpsTime(null, 0, "Not an MP4/MOV file (no moov box).");
            }
            double duration = ReadMovieDuration(stream, moov.Value);

            foreach (Box trak in Children(stream, moov.Value.DataStart, moov.Value.End).Where(b => b.Type == "trak"))
            {
                Box? mdia = FindChild(stream, trak.DataStart, trak.End, "mdia");
                Box? mdhd = mdia == null ? null : FindChild(stream, mdia.Value.DataStart, mdia.Value.End, "mdhd");
                Box? minf = mdia == null ? null : FindChild(stream, mdia.Value.DataStart, mdia.Value.End, "minf");
                Box? stbl = minf == null ? null : FindChild(stream, minf.Value.DataStart, minf.Value.End, "stbl");
                if (mdhd == null || stbl == null || !IsGpmdTrack(stream, stbl.Value))
                {
                    continue;
                }
                uint timescale = ReadTimescale(stream, mdhd.Value);
                if (timescale == 0)
                {
                    continue;
                }
                List<Sample> samples = ReadSampleTable(stream, stbl.Value, timescale);
                return EstimateFromSamples(stream, samples) with { DurationSeconds = duration };
            }
            return new VideoGpsTime(null, 0, "No GoPro telemetry (GPMF) track in this video.", duration);
        }

        /// <summary>The movie header's duration in seconds (0 if missing).</summary>
        private static double ReadMovieDuration(Stream stream, Box moov)
        {
            Box? mvhd = FindChild(stream, moov.DataStart, moov.End, "mvhd");
            if (mvhd == null)
            {
                return 0;
            }
            byte[] data = ReadBoxData(stream, mvhd.Value);
            // v0: version/flags, creation, modification, timescale, duration (32-bit);
            // v1: the times and duration are 64-bit
            if (data.Length >= 32 && data[0] == 1)
            {
                uint scale = BE32(data, 20);
                return scale == 0 ? 0 : (double)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(24)) / scale;
            }
            if (data.Length >= 20)
            {
                uint scale = BE32(data, 12);
                return scale == 0 ? 0 : (double)BE32(data, 16) / scale;
            }
            return 0;
        }

        // ===== GPMF =====

        private static VideoGpsTime EstimateFromSamples(Stream stream, List<Sample> samples)
        {
            List<double> candidates = new();
            bool sawGps = false;
            foreach (Sample sample in samples)
            {
                if (sample.Size <= 0 || sample.Size > 4 * 1024 * 1024 || sample.Offset + sample.Size > stream.Length)
                {
                    continue;
                }
                byte[] data = new byte[sample.Size];
                stream.Position = sample.Offset;
                stream.ReadExactly(data);
                StreamState state = new();
                WalkKlv(data, 0, data.Length, sample, state, candidates, ref sawGps);
                FlushPendingUtc(state, sample, candidates); // a GPSU outside any STRM
            }
            if (candidates.Count == 0)
            {
                return new VideoGpsTime(null, 0, sawGps
                    ? "The camera's GPS never got a fix during this video."
                    : "This video's telemetry has no GPS time (GPS off on the camera?).");
            }
            candidates.Sort();
            return new VideoGpsTime(candidates[candidates.Count / 2], candidates.Count,
                $"GoPro GPS time from {candidates.Count} sample(s)");
        }

        /// <summary>The per-stream values that apply to the GPS entries after them.</summary>
        private class StreamState
        {
            public uint? Fix;            // GPSF
            public double[]? Scale;      // SCAL
            public string? Type;         // TYPE (GPS9's field layout)
            public double? PendingUtc;   // GPSU seen before GPSF - judged once the stream ends
        }

        private static void WalkKlv(byte[] data, int start, int end, Sample sample, StreamState state,
                                    List<double> candidates, ref bool sawGps)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                string key = Encoding.ASCII.GetString(data, pos, 4);
                char type = (char)data[pos + 4];
                int structSize = data[pos + 5];
                int repeat = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 6));
                int length = structSize * repeat;
                int dataStart = pos + 8;
                int next = dataStart + ((length + 3) & ~3);
                if (key == "\0\0\0\0" || dataStart + length > end)
                {
                    break; // padding or a truncated entry
                }

                if (type == '\0')
                {
                    // nested container: a STRM starts a fresh scope for SCAL/TYPE/GPSF
                    StreamState inner = key == "STRM" ? new StreamState() : state;
                    WalkKlv(data, dataStart, dataStart + length, sample, inner, candidates, ref sawGps);
                    if (key == "STRM")
                    {
                        FlushPendingUtc(inner, sample, candidates);
                    }
                }
                else
                {
                    switch (key)
                    {
                        case "GPSF":
                            if (length >= 4)
                            {
                                state.Fix = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataStart));
                            }
                            break;
                        case "SCAL":
                            state.Scale = ReadNumbers(data, dataStart, type, structSize, repeat);
                            break;
                        case "TYPE":
                            state.Type = Encoding.ASCII.GetString(data, dataStart, length).TrimEnd('\0');
                            break;
                        case "GPSU":
                            sawGps = true;
                            if (TryParseGpsu(Encoding.ASCII.GetString(data, dataStart, Math.Min(length, 16)), out double utc))
                            {
                                state.PendingUtc = utc;
                            }
                            break;
                        case "GPS9":
                            sawGps = true;
                            ReadGps9(data, dataStart, structSize, repeat, sample, state, candidates);
                            break;
                    }
                }
                pos = next;
            }
        }

        /// <summary>A GPSU counts once its stream is fully read, so a GPSF after it still
        /// applies; no GPSF at all is taken as usable (some firmware omits it).</summary>
        private static void FlushPendingUtc(StreamState state, Sample sample, List<double> candidates)
        {
            if (state.PendingUtc.HasValue && (!state.Fix.HasValue || state.Fix.Value >= 2))
            {
                candidates.Add(state.PendingUtc.Value - sample.StartSeconds);
            }
            state.PendingUtc = null;
        }

        /// <summary>"yymmddhhmmss.sss", UTC.</summary>
        public static bool TryParseGpsu(string text, out double unixSeconds)
        {
            unixSeconds = 0;
            if (text.Length < 12 ||
                !DateTime.TryParseExact(text[..12], "yyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime whole))
            {
                return false;
            }
            double fraction = 0;
            if (text.Length > 13 && text[12] == '.')
            {
                double.TryParse("0" + text[12..].TrimEnd('\0', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out fraction);
            }
            if (whole.Year < 2010)
            {
                return false; // camera clock never set from GPS
            }
            unixSeconds = new DateTimeOffset(whole, TimeSpan.Zero).ToUnixTimeSeconds() + fraction;
            return true;
        }

        /// <summary>
        /// GPS9: one struct per GPS sample, laid out by the stream's TYPE string (normally
        /// "lllllllSS": lat, lon, alt, 2D speed, 3D speed, days since 2000, seconds since
        /// midnight, DOP, fix) and scaled by SCAL. The samples are spread evenly across the
        /// payload's duration.
        /// </summary>
        private static void ReadGps9(byte[] data, int start, int structSize, int repeat, Sample sample,
                                     StreamState state, List<double> candidates)
        {
            string layout = state.Type ?? "lllllllSS";
            int[] offsets = new int[layout.Length];
            int offset = 0;
            for (int i = 0; i < layout.Length; i++)
            {
                offsets[i] = offset;
                offset += TypeSize(layout[i]);
            }
            if (layout.Length < 9 || offset > structSize)
            {
                return;
            }
            double Scale(int field) => state.Scale is { Length: > 0 } s ? s[Math.Min(field, s.Length - 1)] : 1;
            for (int k = 0; k < repeat; k++)
            {
                int baseOffset = start + k * structSize;
                double days = ReadNumber(data, baseOffset + offsets[5], layout[5]) / Scale(5);
                double seconds = ReadNumber(data, baseOffset + offsets[6], layout[6]) / Scale(6);
                double fix = ReadNumber(data, baseOffset + offsets[8], layout[8]) / Scale(8);
                if (fix < 2 || days < 3650) // no fix, or a clock still at 2000
                {
                    continue;
                }
                double utc = 946684800.0 + days * 86400.0 + seconds; // 2000-01-01T00:00:00Z
                double mediaTime = sample.StartSeconds + sample.DurationSeconds * k / repeat;
                candidates.Add(utc - mediaTime);
            }
        }

        private static int TypeSize(char type) => type switch
        {
            'b' or 'B' or 'c' => 1,
            's' or 'S' => 2,
            'l' or 'L' or 'f' or 'q' => 4,
            'd' or 'j' or 'J' or 'Q' => 8,
            _ => 4,
        };

        private static double ReadNumber(byte[] data, int pos, char type) => type switch
        {
            'b' => (sbyte)data[pos],
            'B' or 'c' => data[pos],
            's' => BinaryPrimitives.ReadInt16BigEndian(data.AsSpan(pos)),
            'S' => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)),
            'l' => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos)),
            'L' => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)),
            'f' => BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(pos)),
            'd' => BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(pos)),
            'j' => BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos)),
            'J' => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos)),
            _ => 0,
        };

        private static double[] ReadNumbers(byte[] data, int start, char type, int structSize, int repeat)
        {
            int size = TypeSize(type);
            int count = size == 0 ? 0 : structSize * repeat / size;
            double[] values = new double[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = ReadNumber(data, start + i * size, type);
            }
            return values;
        }

        // ===== MP4 =====

        private readonly record struct Box(string Type, long DataStart, long End);
        private readonly record struct Sample(long Offset, int Size, double StartSeconds, double DurationSeconds);

        private static IEnumerable<Box> Children(Stream stream, long start, long end)
        {
            long pos = start;
            byte[] header = new byte[16];
            while (pos + 8 <= end)
            {
                stream.Position = pos;
                stream.ReadExactly(header, 0, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                string type = Encoding.ASCII.GetString(header, 4, 4);
                long dataStart = pos + 8;
                if (size == 1)
                {
                    stream.ReadExactly(header, 8, 8);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    dataStart = pos + 16;
                }
                else if (size == 0)
                {
                    size = end - pos; // extends to the end of its parent
                }
                if (size < dataStart - pos || pos + size > end)
                {
                    yield break; // malformed - stop rather than read garbage
                }
                yield return new Box(type, dataStart, pos + size);
                pos += size;
            }
        }

        private static Box? FindChild(Stream stream, long start, long end, string type)
        {
            foreach (Box box in Children(stream, start, end))
            {
                if (box.Type == type)
                {
                    return box;
                }
            }
            return null;
        }

        private static byte[] ReadBoxData(Stream stream, Box box)
        {
            long length = box.End - box.DataStart;
            if (length > 64 * 1024 * 1024)
            {
                throw new InvalidDataException($"'{box.Type}' box is implausibly large");
            }
            byte[] data = new byte[length];
            stream.Position = box.DataStart;
            stream.ReadExactly(data);
            return data;
        }

        private static bool IsGpmdTrack(Stream stream, Box stbl)
        {
            Box? stsd = FindChild(stream, stbl.DataStart, stbl.End, "stsd");
            if (stsd == null)
            {
                return false;
            }
            // version/flags (4), entry count (4), then the first entry: size (4), format (4)
            byte[] data = ReadBoxData(stream, stsd.Value);
            return data.Length >= 16 && Encoding.ASCII.GetString(data, 12, 4) == "gpmd";
        }

        private static uint ReadTimescale(Stream stream, Box mdhd)
        {
            byte[] data = ReadBoxData(stream, mdhd);
            if (data.Length < 24)
            {
                return 0;
            }
            // version 1 has 64-bit creation/modification times
            int timescaleOffset = data[0] == 1 ? 20 : 12;
            return data.Length >= timescaleOffset + 4 ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(timescaleOffset)) : 0;
        }

        private static List<Sample> ReadSampleTable(Stream stream, Box stbl, uint timescale)
        {
            byte[]? stts = Table(stream, stbl, "stts");
            byte[]? stsz = Table(stream, stbl, "stsz");
            byte[]? stsc = Table(stream, stbl, "stsc");
            byte[]? stco = Table(stream, stbl, "stco");
            byte[]? co64 = stco == null ? Table(stream, stbl, "co64") : null;
            if (stts == null || stsz == null || stsc == null || (stco == null && co64 == null))
            {
                return new List<Sample>();
            }

            // sizes
            uint fixedSize = BE32(stsz, 4);
            int sampleCount = (int)BE32(stsz, 8);
            int[] sizes = new int[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                sizes[i] = fixedSize != 0 ? (int)fixedSize : (int)BE32(stsz, 12 + i * 4);
            }

            // start times and durations
            double[] starts = new double[sampleCount];
            double[] durations = new double[sampleCount];
            int sttsEntries = (int)BE32(stts, 4);
            long time = 0;
            int index = 0;
            for (int e = 0; e < sttsEntries && index < sampleCount; e++)
            {
                uint count = BE32(stts, 8 + e * 8);
                uint delta = BE32(stts, 12 + e * 8);
                for (uint c = 0; c < count && index < sampleCount; c++, index++)
                {
                    starts[index] = (double)time / timescale;
                    durations[index] = (double)delta / timescale;
                    time += delta;
                }
            }

            // chunk offsets
            int chunkCount = (int)BE32(stco ?? co64!, 4);
            long[] chunkOffsets = new long[chunkCount];
            for (int c = 0; c < chunkCount; c++)
            {
                chunkOffsets[c] = stco != null ? BE32(stco, 8 + c * 4) : (long)BinaryPrimitives.ReadUInt64BigEndian(co64!.AsSpan(8 + c * 8));
            }

            // samples per chunk (runs of chunks), walked to place every sample in the file
            List<Sample> samples = new(sampleCount);
            int stscEntries = (int)BE32(stsc, 4);
            int sample = 0;
            for (int e = 0; e < stscEntries; e++)
            {
                int firstChunk = (int)BE32(stsc, 8 + e * 12) - 1;
                int perChunk = (int)BE32(stsc, 12 + e * 12);
                int lastChunk = e + 1 < stscEntries ? (int)BE32(stsc, 8 + (e + 1) * 12) - 1 : chunkCount;
                for (int chunk = firstChunk; chunk < lastChunk && chunk < chunkCount; chunk++)
                {
                    long offset = chunkOffsets[chunk];
                    for (int s = 0; s < perChunk && sample < sampleCount; s++, sample++)
                    {
                        samples.Add(new Sample(offset, sizes[sample], starts[sample], durations[sample]));
                        offset += sizes[sample];
                    }
                }
            }
            return samples;
        }

        private static byte[]? Table(Stream stream, Box stbl, string type)
        {
            Box? box = FindChild(stream, stbl.DataStart, stbl.End, type);
            return box == null ? null : ReadBoxData(stream, box.Value);
        }

        private static uint BE32(byte[] data, int offset) =>
            offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)) : 0;
    }
}
