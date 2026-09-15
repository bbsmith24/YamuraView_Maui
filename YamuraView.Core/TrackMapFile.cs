using System.Globalization;
using System.Xml.Linq;

namespace YamuraView.Core
{
    /// <summary>
    /// Reads and writes ".ytm" (Yamura Track Map) files - the XML on-disk form of a
    /// <see cref="TrackMap"/>. The format mirrors the app's other XML config files: numbers
    /// are written with invariant-culture formatting so a map authored under one locale reads
    /// back identically under another. Optional per-point fields are omitted rather than
    /// written empty. UI-independent: callers own file picking and error display.
    /// </summary>
    public static class TrackMapFile
    {
        public const string Extension = ".ytm";

        /// <summary>Writes <paramref name="map"/> to <paramref name="fileName"/>, overwriting.</summary>
        public static void Write(TrackMap map, string fileName)
        {
            XElement root = new("TrackMap",
                new XAttribute("name", map.Name),
                new XAttribute("units", map.Units.ToString().ToLowerInvariant()),
                new XAttribute("sameStartFinish", map.SameStartFinish ? "true" : "false"));
            if (map.Created.HasValue)
            {
                root.Add(new XAttribute("created", map.Created.Value.ToString("o", CultureInfo.InvariantCulture)));
            }

            XElement walk = new("Walk");
            foreach (TrackPoint p in map.Walk)
            {
                XElement pt = new("P",
                    new XAttribute("lat", Fmt(p.Latitude)),
                    new XAttribute("lon", Fmt(p.Longitude)),
                    new XAttribute("t", Fmt(p.TimeSeconds)));
                AddOptional(pt, "spd", p.Speed);
                AddOptional(pt, "crs", p.Course);
                AddOptional(pt, "acc", p.AccuracyMeters);
                AddOptional(pt, "alt", p.Altitude);
                walk.Add(pt);
            }
            root.Add(walk);

            XElement lines = new("Lines");
            foreach (TrackLine line in map.Lines)
            {
                XElement el = new("Line",
                    new XAttribute("type", line.Type.ToString()),
                    new XAttribute("lat", Fmt(line.Latitude)),
                    new XAttribute("lon", Fmt(line.Longitude)),
                    new XAttribute("heading", Fmt(line.Heading)),
                    new XAttribute("width", Fmt(line.Width)));
                if (line.Type == LineType.Sector)
                {
                    el.Add(new XAttribute("order", line.Order.ToString(CultureInfo.InvariantCulture)));
                }
                lines.Add(el);
            }
            root.Add(lines);

            XElement notes = new("Notes");
            foreach (TrackNote note in map.Notes)
            {
                notes.Add(new XElement("Note",
                    new XAttribute("lat", Fmt(note.Latitude)),
                    new XAttribute("lon", Fmt(note.Longitude)),
                    new XAttribute("text", note.Text)));
            }
            root.Add(notes);

            XElement marks = new("Marks");
            foreach (TrackMark mark in map.Marks)
            {
                marks.Add(new XElement("Mark",
                    new XAttribute("lat", Fmt(mark.Latitude)),
                    new XAttribute("lon", Fmt(mark.Longitude)),
                    new XAttribute("shape", mark.Shape.ToString()),
                    new XAttribute("orient", Fmt(mark.Orientation))));
            }
            root.Add(marks);

            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(fileName);
        }

        /// <summary>Reads a track map from <paramref name="fileName"/>. Throws on a missing or
        /// malformed file (no root TrackMap element); individual malformed points/lines are
        /// skipped rather than failing the whole read.</summary>
        public static TrackMap Read(string fileName)
        {
            XDocument doc = XDocument.Load(fileName);
            XElement root = doc.Element("TrackMap")
                ?? throw new FormatException($"{fileName}: not a TrackMap file (no <TrackMap> root).");

            TrackMap map = new()
            {
                Name = (string?)root.Attribute("name") ?? "",
                Units = string.Equals((string?)root.Attribute("units"), "meters", StringComparison.OrdinalIgnoreCase)
                    ? TrackMapUnits.Meters : TrackMapUnits.Feet,
                SameStartFinish = string.Equals((string?)root.Attribute("sameStartFinish"), "true", StringComparison.OrdinalIgnoreCase),
            };
            string? createdStr = (string?)root.Attribute("created");
            if (!string.IsNullOrWhiteSpace(createdStr) &&
                DateTime.TryParse(createdStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime created))
            {
                map.Created = created;
            }

            foreach (XElement pt in root.Element("Walk")?.Elements("P") ?? Enumerable.Empty<XElement>())
            {
                if (TryDouble(pt, "lat", out double lat) && TryDouble(pt, "lon", out double lon))
                {
                    map.Walk.Add(new TrackPoint
                    {
                        Latitude = lat,
                        Longitude = lon,
                        TimeSeconds = TryFloat(pt, "t", out float t) ? t : 0f,
                        Speed = OptFloat(pt, "spd"),
                        Course = OptFloat(pt, "crs"),
                        AccuracyMeters = OptFloat(pt, "acc"),
                        Altitude = OptFloat(pt, "alt"),
                    });
                }
            }

            foreach (XElement el in root.Element("Lines")?.Elements("Line") ?? Enumerable.Empty<XElement>())
            {
                if (!Enum.TryParse((string?)el.Attribute("type"), out LineType type) ||
                    !TryDouble(el, "lat", out double lat) || !TryDouble(el, "lon", out double lon))
                {
                    continue;
                }
                map.Lines.Add(new TrackLine
                {
                    Type = type,
                    Latitude = lat,
                    Longitude = lon,
                    Heading = TryFloat(el, "heading", out float h) ? h : 0f,
                    Width = TryFloat(el, "width", out float w) ? w : 0f,
                    Order = int.TryParse((string?)el.Attribute("order"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int o) ? o : 0,
                });
            }

            foreach (XElement el in root.Element("Notes")?.Elements("Note") ?? Enumerable.Empty<XElement>())
            {
                if (TryDouble(el, "lat", out double lat) && TryDouble(el, "lon", out double lon))
                {
                    map.Notes.Add(new TrackNote
                    {
                        Latitude = lat,
                        Longitude = lon,
                        Text = (string?)el.Attribute("text") ?? "",
                    });
                }
            }

            foreach (XElement el in root.Element("Marks")?.Elements("Mark") ?? Enumerable.Empty<XElement>())
            {
                if (TryDouble(el, "lat", out double lat) && TryDouble(el, "lon", out double lon))
                {
                    map.Marks.Add(new TrackMark
                    {
                        Latitude = lat,
                        Longitude = lon,
                        Shape = Enum.TryParse((string?)el.Attribute("shape"), out MarkShape shape) ? shape : MarkShape.Square,
                        Orientation = TryFloat(el, "orient", out float o) ? o : 0f,
                    });
                }
            }

            return map;
        }

        private static void AddOptional(XElement el, string name, float? value)
        {
            if (value.HasValue)
            {
                el.Add(new XAttribute(name, Fmt(value.Value)));
            }
        }

        private static string Fmt(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Fmt(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static bool TryDouble(XElement el, string name, out double value) =>
            double.TryParse((string?)el.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static bool TryFloat(XElement el, string name, out float value) =>
            float.TryParse((string?)el.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static float? OptFloat(XElement el, string name) =>
            TryFloat(el, name, out float value) ? value : null;
    }
}
