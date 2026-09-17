using System.Globalization;
using System.Text;

namespace YamuraView.Core
{
    /// <summary>Delimited text format a run is exported to.</summary>
    public enum DelimitedFormat
    {
        Csv,
        Tsv,
    }

    /// <summary>
    /// Exports a loaded run's channel data as a delimited text file (CSV or TSV). The first
    /// column is Time (the sample timestamp), followed by one column per channel. Rows are the
    /// union of every channel's sample timestamps in ascending order; a channel with no sample
    /// at a given timestamp leaves that cell blank. The internal "Time" channel is omitted as a
    /// data column since it duplicates the timestamp key. Values are written with invariant
    /// culture so the file parses the same regardless of the machine's regional settings.
    /// </summary>
    public static class LogFileExporter
    {
        /// <summary>File extension (including the dot) for the given format.</summary>
        public static string Extension(DelimitedFormat format) => format == DelimitedFormat.Tsv ? ".tsv" : ".csv";

        private static char Delimiter(DelimitedFormat format) => format == DelimitedFormat.Tsv ? '\t' : ',';

        /// <summary>
        /// Builds the full delimited text for one run. The header row starts with "Time" and
        /// lists the channel names (sorted, case-insensitive); each following row is one
        /// timestamp with each channel's value at that timestamp (blank where absent).
        /// </summary>
        public static string BuildText(RunData run, DelimitedFormat format)
        {
            char delimiter = Delimiter(format);

            // channel columns: every channel except the internal Time channel (its value is the
            // timestamp, which is already the first column), sorted for a stable column order
            List<string> channelNames = run.channels.Keys
                .Where(n => !n.Equals("Time", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // union of all sample timestamps across the exported channels (plus the Time
            // channel, so a run whose only channel is Time still gets its rows)
            SortedSet<float> timestamps = new();
            foreach (string name in channelNames)
            {
                foreach (float t in run.channels[name].DataPoints.Keys)
                {
                    timestamps.Add(t);
                }
            }
            if (run.channels.TryGetValue("Time", out DataChannel? timeChannel))
            {
                foreach (float t in timeChannel.DataPoints.Keys)
                {
                    timestamps.Add(t);
                }
            }

            StringBuilder sb = new();
            sb.Append("Time");
            foreach (string name in channelNames)
            {
                sb.Append(delimiter);
                sb.Append(Escape(name, delimiter));
            }
            sb.Append('\n');

            foreach (float t in timestamps)
            {
                sb.Append(t.ToString(CultureInfo.InvariantCulture));
                foreach (string name in channelNames)
                {
                    sb.Append(delimiter);
                    if (run.channels[name].DataPoints.TryGetValue(t, out float value))
                    {
                        sb.Append(value.ToString(CultureInfo.InvariantCulture));
                    }
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The output path for a run: the source log file's path with the format's extension.
        /// Falls back to the run name in the current directory if the run has no source path.
        /// </summary>
        public static string OutputPath(RunData run, DelimitedFormat format)
        {
            string basePath = !string.IsNullOrEmpty(run.fileName) ? run.fileName : run.runName;
            return Path.ChangeExtension(basePath, Extension(format));
        }

        /// <summary>
        /// Escapes a header field: for CSV, a field containing the delimiter, a quote, or a
        /// newline is quoted with embedded quotes doubled; for TSV, tabs/newlines in a name are
        /// replaced with spaces. Channel names rarely need this, but it keeps the header valid.
        /// </summary>
        private static string Escape(string field, char delimiter)
        {
            if (delimiter == '\t')
            {
                return field.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            }
            if (field.IndexOf(delimiter) >= 0 || field.IndexOf('"') >= 0 || field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0)
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }
            return field;
        }
    }
}
