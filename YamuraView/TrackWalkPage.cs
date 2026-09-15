using YamuraView.Core;

namespace YamuraView;

/// <summary>
/// Track-walk capture page: records the device's GPS as a breadcrumb trail while the user walks
/// the track, then lets them place start/finish/sector lines and position notes on the rendered
/// trail (capture-then-place) and save it as a ".ytm" track map. Only useful on a device with a
/// GPS receiver; the saved map is imported for analysis anywhere (see <see cref="TrackMapGeometry"/>
/// / <see cref="TrackMapAlignment"/>). Built in code-behind, mirroring
/// <see cref="AlignmentWizardPage"/>'s interactive-map pattern (Tap places/selects, Pan drags the
/// selected item - the gesture pair that works for both touch and mouse).
/// </summary>
public sealed class TrackWalkPage : ContentPage
{
    private enum Tool { Select, Start, Sector, Finish, Note }

    private const double SelectPixelThreshold = 24.0;
    private const float DefaultWidth = 30f; // in the map's current units

    private readonly TrackMap map;
    private readonly bool allowRecording;
    private readonly TrackWalkDrawable drawable;
    private readonly GraphicsView mapView;

    private bool recording;
    private DateTimeOffset? firstFixTime;
    private EventHandler<GeolocationLocationChangedEventArgs>? locationHandler;

    private Tool tool = Tool.Select;
    private TrackLine? selectedLine;
    private TrackNote? selectedNote;
    private TrackMark? selectedMark;

    /// <summary>Fill color for placed marks; set by the caller from settings (default orange).</summary>
    public Color MarkColor
    {
        get => drawable.MarkColor;
        set => drawable.MarkColor = value;
    }

    // drag state (Pan on the selected item)
    private bool dragging;
    private PointF dragAnchorPixel;

    private bool updatingFields; // guards entry-change handlers while we set values programmatically

    private readonly Button recordButton = new() { Text = "● Record" };
    private readonly Entry nameEntry = new() { Placeholder = "Track name", WidthRequest = 160 };
    private readonly Picker unitsPicker = new() { WidthRequest = 90 };
    private readonly Switch sameStartFinishSwitch = new();
    private readonly Label selectionLabel = new() { VerticalOptions = LayoutOptions.Center };
    private readonly List<(Tool Tool, Button Button)> toolButtons = new();

    private readonly Grid linePropsRow;
    private readonly Grid notePropsRow;
    private readonly Grid markPropsRow;
    private readonly Entry headingEntry = new() { Keyboard = Keyboard.Numeric, WidthRequest = 80 };
    private readonly Entry widthEntry = new() { Keyboard = Keyboard.Numeric, WidthRequest = 80 };
    private readonly Entry noteTextEntry = new() { Placeholder = "Note text", HorizontalOptions = LayoutOptions.Fill };
    private readonly Picker markShapePicker = new() { WidthRequest = 110 };
    private readonly Entry markOrientEntry = new() { Keyboard = Keyboard.Numeric, WidthRequest = 80 };

    /// <summary>Live track walk: records GPS into a fresh map.</summary>
    public TrackWalkPage() : this(new TrackMap { Units = TrackMapUnits.Feet, SameStartFinish = false }, allowRecording: true)
    {
    }

    /// <summary>
    /// Edits <paramref name="seedMap"/>. With <paramref name="allowRecording"/> false the live GPS
    /// controls are hidden - used when the trail is seeded from an existing run
    /// (<see cref="TrackMapBuilder.FromRun"/>) so the user only places lines/notes and saves.
    /// </summary>
    public TrackWalkPage(TrackMap seedMap, bool allowRecording)
    {
        map = seedMap;
        this.allowRecording = allowRecording;
        Title = allowRecording ? "Track Walk" : "Track Map from Run";
        drawable = new TrackWalkDrawable { Map = map };
        mapView = new GraphicsView { Drawable = drawable };

        Label header = new()
        {
            Text = allowRecording ? "Record a Track Walk" : "Track Map from Run",
            FontAttributes = FontAttributes.Bold,
            FontSize = 16
        };
        Label instructions = new()
        {
            Text = allowRecording
                ? "Tap Record and walk the track to capture the GPS trail. Then pick a line type "
                    + "and tap the trail to drop Start / Sector / Finish lines and Notes; tap Select and "
                    + "drag to reposition, and edit heading/width below. Save writes a .ytm you can import "
                    + "on any device for start, finish, and delta timing."
                : "The trail is the run's recorded GPS. Pick a line type and tap the trail to drop "
                    + "Start / Sector / Finish lines and Notes; tap Select and drag to reposition, and "
                    + "edit heading/width below. Save writes a .ytm you can import on any device for "
                    + "start, finish, and delta timing.",
            FontSize = 13
        };

        recordButton.Clicked += OnRecordClicked;
        recordButton.IsVisible = allowRecording;

        nameEntry.Text = map.Name;
        unitsPicker.ItemsSource = new List<string> { "Feet", "Meters" };
        unitsPicker.SelectedIndex = map.Units == TrackMapUnits.Meters ? 1 : 0;
        unitsPicker.SelectedIndexChanged += (_, _) =>
        {
            map.Units = unitsPicker.SelectedIndex == 1 ? TrackMapUnits.Meters : TrackMapUnits.Feet;
            mapView.Invalidate();
        };

        sameStartFinishSwitch.Toggled += (_, e) =>
        {
            map.SameStartFinish = e.Value;
            // a circuit uses one Start line as start/finish - drop any separate Finish
            if (e.Value)
            {
                map.Lines.RemoveAll(l => l.Type == LineType.Finish);
                if (ReferenceEquals(selectedLine, null) == false && selectedLine!.Type == LineType.Finish)
                {
                    ClearSelection();
                }
            }
            UpdateToolButtons();
            mapView.Invalidate();
        };

        HorizontalStackLayout recordRow = new() { Spacing = 8 };
        recordRow.Add(recordButton);
        recordRow.Add(nameEntry);
        recordRow.Add(new Label { Text = "Units", VerticalOptions = LayoutOptions.Center });
        recordRow.Add(unitsPicker);
        recordRow.Add(new Label { Text = "Circuit (start=finish)", VerticalOptions = LayoutOptions.Center });
        recordRow.Add(sameStartFinishSwitch);

        FlexLayout toolRow = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        AddToolButton(toolRow, Tool.Select, "Select");
        AddToolButton(toolRow, Tool.Start, "+ Start");
        AddToolButton(toolRow, Tool.Sector, "+ Sector");
        AddToolButton(toolRow, Tool.Finish, "+ Finish");
        AddToolButton(toolRow, Tool.Note, "+ Note");
        // "Mark Location" is an action, not a placement mode: it drops a mark at the current GPS
        // fix (while recording) or the map center, so you can flag a cone/apex as you walk past it.
        Button markButton = new() { Text = "Mark Location", Margin = new Thickness(0, 0, 6, 6) };
        markButton.Clicked += OnMarkLocationClicked;
        toolRow.Add(markButton);

        // gestures: Tap places (a placement tool) or selects (Select); Pan drags the selection
        TapGestureRecognizer tap = new();
        tap.Tapped += (_, e) => OnMapTapped(e.GetPosition(mapView));
        mapView.GestureRecognizers.Add(tap);
        PanGestureRecognizer pan = new();
        pan.PanUpdated += OnPanUpdated;
        mapView.GestureRecognizers.Add(pan);

        linePropsRow = BuildLinePropsRow();
        notePropsRow = BuildNotePropsRow();
        markPropsRow = BuildMarkPropsRow();
        linePropsRow.IsVisible = false;
        notePropsRow.IsVisible = false;
        markPropsRow.IsVisible = false;

        Button cancelButton = new() { Text = "Cancel" };
        cancelButton.Clicked += async (_, _) => { StopRecording(); await Navigation.PopModalAsync(); };
        Button saveButton = new() { Text = "Save .ytm" };
        saveButton.Clicked += OnSaveClicked;
        HorizontalStackLayout buttonRow = new() { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        buttonRow.Add(cancelButton);
        buttonRow.Add(saveButton);

        Grid layout = new()
        {
            Padding = 12,
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        layout.Add(header, 0, 0);
        layout.Add(instructions, 0, 1);
        layout.Add(recordRow, 0, 2);
        layout.Add(toolRow, 0, 3);
        layout.Add(mapView, 0, 4);
        layout.Add(selectionLabel, 0, 5);
        layout.Add(linePropsRow, 0, 6);
        layout.Add(notePropsRow, 0, 6); // same row; only one visible at a time
        layout.Add(markPropsRow, 0, 6);
        layout.Add(buttonRow, 0, 7);
        Content = layout;

        sameStartFinishSwitch.IsToggled = map.SameStartFinish;
        UpdateToolButtons();
        UpdateSelectionLabel();
    }

    // ---- tool buttons ----

    private void AddToolButton(Layout parent, Tool t, string text)
    {
        Button b = new() { Text = text, Margin = new Thickness(0, 0, 6, 6) };
        b.Clicked += (_, _) => { tool = t; UpdateToolButtons(); };
        toolButtons.Add((t, b));
        parent.Add(b);
    }

    private void UpdateToolButtons()
    {
        foreach ((Tool t, Button b) in toolButtons)
        {
            bool enabled = !(t == Tool.Finish && map.SameStartFinish);
            b.IsEnabled = enabled;
            b.BackgroundColor = t == tool ? Colors.CornflowerBlue : null;
            b.TextColor = t == tool ? Colors.White : null;
        }
    }

    // ---- recording ----

    private async void OnRecordClicked(object? sender, EventArgs e)
    {
        if (recording)
        {
            StopRecording();
            return;
        }
        try
        {
            PermissionStatus status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("Track Walk", "Location permission is required to record a track walk.", "OK");
                return;
            }

            locationHandler = OnLocationChanged;
            Geolocation.Default.LocationChanged += locationHandler;
            bool started = await Geolocation.Default.StartListeningForegroundAsync(
                new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
            if (!started)
            {
                Geolocation.Default.LocationChanged -= locationHandler;
                locationHandler = null;
                await DisplayAlertAsync("Track Walk", "Couldn't start location updates on this device.", "OK");
                return;
            }
            recording = true;
            recordButton.Text = "■ Stop";
            recordButton.TextColor = Colors.Red;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Track Walk", $"Location isn't available: {ex.Message}", "OK");
        }
    }

    private void StopRecording()
    {
        if (!recording)
        {
            return;
        }
        recording = false;
        Geolocation.Default.StopListeningForeground();
        if (locationHandler != null)
        {
            Geolocation.Default.LocationChanged -= locationHandler;
            locationHandler = null;
        }
        recordButton.Text = "● Record";
        recordButton.TextColor = null;
    }

    private void OnLocationChanged(object? sender, GeolocationLocationChangedEventArgs e)
    {
        Location loc = e.Location;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            firstFixTime ??= loc.Timestamp;
            map.Walk.Add(new TrackPoint
            {
                Latitude = loc.Latitude,
                Longitude = loc.Longitude,
                TimeSeconds = (float)(loc.Timestamp - firstFixTime.Value).TotalSeconds,
                Speed = loc.Speed.HasValue ? (float)loc.Speed.Value : null,
                Course = loc.Course.HasValue ? (float)loc.Course.Value : null,
                AccuracyMeters = loc.Accuracy.HasValue ? (float)loc.Accuracy.Value : null,
                Altitude = loc.Altitude.HasValue ? (float)loc.Altitude.Value : null,
            });
            drawable.CurrentPosition = (loc.Latitude, loc.Longitude);
            mapView.Invalidate();
        });
    }

    // ---- placement / selection ----

    private void OnMapTapped(Point? position)
    {
        if (!position.HasValue)
        {
            return;
        }
        (double Lat, double Lon)? geo = drawable.PixelToGeo(position.Value);
        if (!geo.HasValue)
        {
            return;
        }

        if (tool == Tool.Select)
        {
            SelectNearest(position.Value);
            return;
        }
        if (tool == Tool.Note)
        {
            TrackNote note = new() { Latitude = geo.Value.Lat, Longitude = geo.Value.Lon, Text = "" };
            map.Notes.Add(note);
            SwitchToSelect();
            SelectNote(note);
        }
        else
        {
            LineType type = tool switch
            {
                Tool.Start => LineType.Start,
                Tool.Finish => LineType.Finish,
                _ => LineType.Sector,
            };
            // only one Start / Finish; replace an existing one
            if (type is LineType.Start or LineType.Finish)
            {
                map.Lines.RemoveAll(l => l.Type == type);
            }
            TrackLine line = new()
            {
                Type = type,
                Latitude = geo.Value.Lat,
                Longitude = geo.Value.Lon,
                Heading = DefaultHeadingAt(geo.Value.Lat, geo.Value.Lon),
                Width = DefaultWidth,
                Order = type == LineType.Sector ? NextSectorOrder() : 0,
            };
            map.Lines.Add(line);
            SwitchToSelect();
            SelectLine(line);
        }
        mapView.Invalidate();
    }

    private void SwitchToSelect()
    {
        tool = Tool.Select;
        UpdateToolButtons();
    }

    private int NextSectorOrder() =>
        map.Lines.Where(l => l.Type == LineType.Sector).Select(l => l.Order).DefaultIfEmpty(0).Max() + 1;

    private void SelectNearest(Point p)
    {
        double best = SelectPixelThreshold;
        TrackLine? bestLine = null;
        TrackNote? bestNote = null;
        TrackMark? bestMark = null;
        foreach (TrackLine line in map.Lines)
        {
            PointF? px = drawable.GeoToPixel(line.Latitude, line.Longitude);
            if (px is { } pt)
            {
                double d = Distance(pt, p);
                if (d < best) { best = d; bestLine = line; bestNote = null; bestMark = null; }
            }
        }
        foreach (TrackNote note in map.Notes)
        {
            PointF? px = drawable.GeoToPixel(note.Latitude, note.Longitude);
            if (px is { } pt)
            {
                double d = Distance(pt, p);
                if (d < best) { best = d; bestNote = note; bestLine = null; bestMark = null; }
            }
        }
        foreach (TrackMark mark in map.Marks)
        {
            PointF? px = drawable.GeoToPixel(mark.Latitude, mark.Longitude);
            if (px is { } pt)
            {
                double d = Distance(pt, p);
                if (d < best) { best = d; bestMark = mark; bestLine = null; bestNote = null; }
            }
        }
        if (bestLine != null) { SelectLine(bestLine); }
        else if (bestNote != null) { SelectNote(bestNote); }
        else if (bestMark != null) { SelectMark(bestMark); }
        else { ClearSelection(); }
    }

    /// <summary>Drops a mark at the current GPS fix (while recording) or, failing that, at the
    /// center of the current view - so a cone/apex can be flagged in both live and from-run modes -
    /// then selects it for dragging.</summary>
    private void OnMarkLocationClicked(object? sender, EventArgs e)
    {
        (double Lat, double Lon)? geo = drawable.CurrentPosition is { } cur
            ? (cur.Lat, cur.Lon)
            : drawable.PixelToGeo(new Point(mapView.Width / 2, mapView.Height / 2));
        if (!geo.HasValue)
        {
            return;
        }
        TrackMark mark = new()
        {
            Latitude = geo.Value.Lat,
            Longitude = geo.Value.Lon,
            // start pointing along the walk direction here, so a triangle (pointer cone) is aimed
            // sensibly by default; harmless for a square
            Orientation = DefaultHeadingAt(geo.Value.Lat, geo.Value.Lon),
        };
        map.Marks.Add(mark);
        SwitchToSelect();
        SelectMark(mark);
        mapView.Invalidate();
    }

    private static double Distance(PointF a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void SelectLine(TrackLine line)
    {
        selectedLine = line;
        selectedNote = null;
        selectedMark = null;
        drawable.SelectedLine = line;
        drawable.SelectedNote = null;
        drawable.SelectedMark = null;
        updatingFields = true;
        headingEntry.Text = line.Heading.ToString("0.#");
        widthEntry.Text = line.Width.ToString("0.#");
        updatingFields = false;
        linePropsRow.IsVisible = true;
        notePropsRow.IsVisible = false;
        markPropsRow.IsVisible = false;
        UpdateSelectionLabel();
        mapView.Invalidate();
    }

    private void SelectNote(TrackNote note)
    {
        selectedNote = note;
        selectedLine = null;
        selectedMark = null;
        drawable.SelectedNote = note;
        drawable.SelectedLine = null;
        drawable.SelectedMark = null;
        updatingFields = true;
        noteTextEntry.Text = note.Text;
        updatingFields = false;
        notePropsRow.IsVisible = true;
        linePropsRow.IsVisible = false;
        markPropsRow.IsVisible = false;
        UpdateSelectionLabel();
        mapView.Invalidate();
    }

    private void SelectMark(TrackMark mark)
    {
        selectedMark = mark;
        selectedLine = null;
        selectedNote = null;
        drawable.SelectedMark = mark;
        drawable.SelectedLine = null;
        drawable.SelectedNote = null;
        updatingFields = true;
        markShapePicker.SelectedIndex = mark.Shape == MarkShape.Triangle ? 1 : 0;
        markOrientEntry.Text = mark.Orientation.ToString("0.#");
        updatingFields = false;
        markPropsRow.IsVisible = true;
        linePropsRow.IsVisible = false;
        notePropsRow.IsVisible = false;
        UpdateSelectionLabel();
        mapView.Invalidate();
    }

    private void ClearSelection()
    {
        selectedLine = null;
        selectedNote = null;
        selectedMark = null;
        drawable.SelectedLine = null;
        drawable.SelectedNote = null;
        drawable.SelectedMark = null;
        linePropsRow.IsVisible = false;
        notePropsRow.IsVisible = false;
        markPropsRow.IsVisible = false;
        UpdateSelectionLabel();
        mapView.Invalidate();
    }

    private void UpdateSelectionLabel()
    {
        selectionLabel.Text = selectedLine != null
            ? $"Selected: {selectedLine.Type}{(selectedLine.Type == LineType.Sector ? " " + selectedLine.Order : "")} line"
            : selectedNote != null
                ? "Selected: note"
                : selectedMark != null
                    ? "Selected: mark"
                    : $"Tool: {tool}  •  {map.Walk.Count} trail points  •  drag a selected item to move it";
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        // only Select-tool drags move the selection; placement tools use tap
        if (tool != Tool.Select || (selectedLine == null && selectedNote == null && selectedMark == null))
        {
            return;
        }
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                dragging = true;
                PointF? anchor = selectedLine != null
                    ? drawable.GeoToPixel(selectedLine.Latitude, selectedLine.Longitude)
                    : selectedNote != null
                        ? drawable.GeoToPixel(selectedNote.Latitude, selectedNote.Longitude)
                        : drawable.GeoToPixel(selectedMark!.Latitude, selectedMark.Longitude);
                dragAnchorPixel = anchor ?? new PointF((float)(mapView.Width / 2), (float)(mapView.Height / 2));
                break;
            case GestureStatus.Running:
                if (!dragging)
                {
                    break;
                }
                Point moved = new(dragAnchorPixel.X + e.TotalX, dragAnchorPixel.Y + e.TotalY);
                (double Lat, double Lon)? geo = drawable.PixelToGeo(moved);
                if (geo.HasValue)
                {
                    if (selectedLine != null) { selectedLine.Latitude = geo.Value.Lat; selectedLine.Longitude = geo.Value.Lon; }
                    else if (selectedNote != null) { selectedNote.Latitude = geo.Value.Lat; selectedNote.Longitude = geo.Value.Lon; }
                    else if (selectedMark != null) { selectedMark.Latitude = geo.Value.Lat; selectedMark.Longitude = geo.Value.Lon; }
                    mapView.Invalidate();
                }
                break;
            default:
                dragging = false;
                break;
        }
    }

    // ---- properties rows ----

    private Grid BuildLinePropsRow()
    {
        headingEntry.TextChanged += (_, _) =>
        {
            if (!updatingFields && selectedLine != null && float.TryParse(headingEntry.Text, out float h))
            {
                selectedLine.Heading = ((h % 360f) + 360f) % 360f;
                mapView.Invalidate();
            }
        };
        widthEntry.TextChanged += (_, _) =>
        {
            if (!updatingFields && selectedLine != null && float.TryParse(widthEntry.Text, out float w) && w > 0)
            {
                selectedLine.Width = w;
                mapView.Invalidate();
            }
        };
        Button delete = new() { Text = "Delete line" };
        delete.Clicked += (_, _) =>
        {
            if (selectedLine != null) { map.Lines.Remove(selectedLine); ClearSelection(); }
        };
        Grid g = new()
        {
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star)
            }
        };
        g.Add(new Label { Text = "Heading°", VerticalOptions = LayoutOptions.Center }, 0, 0);
        g.Add(headingEntry, 1, 0);
        g.Add(new Label { Text = "Width", VerticalOptions = LayoutOptions.Center }, 2, 0);
        g.Add(widthEntry, 3, 0);
        g.Add(delete, 4, 0);
        return g;
    }

    private Grid BuildNotePropsRow()
    {
        noteTextEntry.TextChanged += (_, _) =>
        {
            if (!updatingFields && selectedNote != null)
            {
                selectedNote.Text = noteTextEntry.Text ?? "";
            }
        };
        Button delete = new() { Text = "Delete note" };
        delete.Clicked += (_, _) =>
        {
            if (selectedNote != null) { map.Notes.Remove(selectedNote); ClearSelection(); }
        };
        Grid g = new()
        {
            ColumnSpacing = 6,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }
        };
        g.Add(noteTextEntry, 0, 0);
        g.Add(delete, 1, 0);
        return g;
    }

    private Grid BuildMarkPropsRow()
    {
        markShapePicker.ItemsSource = new List<string> { "Square (cone)", "Triangle (pointer)" };
        markShapePicker.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingFields && selectedMark != null)
            {
                selectedMark.Shape = markShapePicker.SelectedIndex == 1 ? MarkShape.Triangle : MarkShape.Square;
                mapView.Invalidate();
            }
        };
        markOrientEntry.TextChanged += (_, _) =>
        {
            if (!updatingFields && selectedMark != null && float.TryParse(markOrientEntry.Text, out float o))
            {
                selectedMark.Orientation = ((o % 360f) + 360f) % 360f;
                mapView.Invalidate();
            }
        };
        Button delete = new() { Text = "Delete mark" };
        delete.Clicked += (_, _) =>
        {
            if (selectedMark != null) { map.Marks.Remove(selectedMark); ClearSelection(); }
        };
        Grid g = new()
        {
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star)
            }
        };
        g.Add(new Label { Text = "Shape", VerticalOptions = LayoutOptions.Center }, 0, 0);
        g.Add(markShapePicker, 1, 0);
        g.Add(new Label { Text = "Orient°", VerticalOptions = LayoutOptions.Center }, 2, 0);
        g.Add(markOrientEntry, 3, 0);
        g.Add(delete, 4, 0);
        return g;
    }

    // ---- save ----

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (map.StartLine == null)
        {
            await DisplayAlertAsync("Save Track Map", "Place a Start line before saving.", "OK");
            return;
        }
        if (!map.SameStartFinish && map.FinishLine == null)
        {
            await DisplayAlertAsync("Save Track Map",
                "Point-to-point maps need a Finish line. Add one, or turn on Circuit (start=finish).", "OK");
            return;
        }

        StopRecording();
        map.Name = string.IsNullOrWhiteSpace(nameEntry.Text) ? "Track Map" : nameEntry.Text.Trim();
        map.Created = DateTime.UtcNow;
        try
        {
            string? path = await TrackMapFilePicker.SaveAsync(map, map.Name);
            if (path != null)
            {
                await DisplayAlertAsync("Save Track Map", $"Saved track map:\n{path}", "OK");
                await Navigation.PopModalAsync();
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Save Track Map", $"Couldn't save: {ex.Message}", "OK");
        }
    }

    /// <summary>Default travel heading for a line placed at a point: the bearing of the walk
    /// trail near it (the direction the walk was going as it passed), or 0 with no trail.</summary>
    private float DefaultHeadingAt(double lat, double lon)
    {
        if (map.Walk.Count < 2)
        {
            return 0f;
        }
        double cosLat = Math.Cos(lat * Math.PI / 180.0);
        int nearest = 0;
        double bestSq = double.MaxValue;
        for (int i = 0; i < map.Walk.Count; i++)
        {
            double de = (map.Walk[i].Longitude - lon) * cosLat;
            double dn = map.Walk[i].Latitude - lat;
            double sq = de * de + dn * dn;
            if (sq < bestSq) { bestSq = sq; nearest = i; }
        }
        int a = Math.Max(0, nearest - 1);
        int b = Math.Min(map.Walk.Count - 1, nearest + 1);
        if (a == b)
        {
            return 0f;
        }
        double east = (map.Walk[b].Longitude - map.Walk[a].Longitude) * cosLat;
        double north = map.Walk[b].Latitude - map.Walk[a].Latitude;
        double bearing = Math.Atan2(east, north) * 180.0 / Math.PI;
        return (float)(((bearing % 360.0) + 360.0) % 360.0);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopRecording();
    }
}
