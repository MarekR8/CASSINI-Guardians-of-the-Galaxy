using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;
using Typeface = Android.Graphics.Typeface;

namespace Cassini.Offline;

[Activity(Label = "CASSINI Offline", MainLauncher = true, Exported = true,
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class MainActivity : Activity
{
    private const int LocationRequest = 1;
    private const int ImportRequest = 2;
    private readonly IEwssReceiver ewss = new MockEwssReceiver();
    private ScrollView screen = null!;
    private LinearLayout panel = null!;
    private LinearLayout demoPanel = null!;
    private TextView ewssBanner = null!;
    private TextView mapLegend = null!;
    private TextView routeStatus = null!;
    private TextView distributionStatus = null!;
    private TextView gnssStatus = null!;
    private TextView layerStatus = null!;
    private TextView exitStatus = null!;
    private TextView residentStatus = null!;
    private LocalExitReports exitReports = null!;
    private DemoMapView map = null!;
    private GalileoReader reader = null!;
    private Button nextStage = null!;
    private Button primaryAction = null!;
    private Button demoToggle = null!;
    private Button viewSwitch = null!;
    private CopernicusLayer? layer;
    private bool showLayer;
    private bool monitorGnss;
    private bool routeWithdrawn;
    private string? savedExitMode;
    private ResidentAction action;
    private string? confirmationMessage;
    private bool awaitingPosition;
    private bool useExercisePosition;
    private int stage;
    private ScheduledWalkingRoute? selected;
    private ISharedPreferences RoutePreferences => GetSharedPreferences("exercise-route", FileCreationMode.Private)!;
    private string LayerPath => Path.Combine(FilesDir!.AbsolutePath, "copernicus.geojson");

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        stage = RoutePreferences.GetInt("stage", 0);
        selected = OfflineScenario.RestoreRoute(stage, RoutePreferences.GetString("exit", null));
        savedExitMode = selected is null ? null : RoutePreferences.GetString("savedExitMode", null);
        routeWithdrawn = RoutePreferences.GetBoolean("routeWithdrawn", false);
        monitorGnss = state?.GetBoolean("monitorGnss") ?? false;
        awaitingPosition = state?.GetBoolean("awaitingPosition") ?? false;
        useExercisePosition = state?.GetBoolean("useExercisePosition") ?? false;

        screen = new ScrollView(this);
        screen.SetBackgroundColor(Color.Rgb(247, 249, 252));
        screen.SetOnApplyWindowInsetsListener(new SafeScreenInsets());
        panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(20), Dp(20), Dp(20), Dp(20));
        screen.AddView(panel);
        SetContentView(screen);

        AddText("CASSINI Offline", 14).SetTextColor(Color.Rgb(61, 82, 107));
        AddHeading("Ewakuacja", 30);
        AddText("Ćwiczenie · mapa i trasa dostępne offline", 14);
        ewssBanner = AddText("", 16);
        ewssBanner.Background = RoundedBackground(Color.Rgb(255, 234, 179));
        ewssBanner.SetTextColor(Color.Rgb(83, 48, 15));
        ewssBanner.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        ewssBanner.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        ewssBanner.Text = state?.GetString("ewssBanner");
        ewssBanner.Visibility = string.IsNullOrEmpty(ewssBanner.Text) ? ViewStates.Gone : ViewStates.Visible;

        map = new DemoMapView(this);
        map.Background = RoundedBackground(Color.Rgb(236, 243, 248));
        map.ClipToOutline = true;
        panel.AddView(map, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(255))
        {
            TopMargin = Dp(16)
        });
        mapLegend = AddText("Niebieski: zalanie · zielony: trasa · czerwony: zamknięcie", 12);
        routeStatus = AddHeading("", 19);
        routeStatus.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        residentStatus = AddText("", 15);
        residentStatus.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        primaryAction = AddButton("", RunPrimaryAction);
        primaryAction.SetTextColor(Color.White);
        primaryAction.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        primaryAction.BackgroundTintList = ColorStateList.ValueOf(Color.Rgb(25, 70, 101));
        primaryAction.SetMinHeight(Dp(56));

        exitReports = new LocalExitReports(Path.Combine(FilesDir!.AbsolutePath, "exit-reports"));
        BuildDemoPanel(state?.GetBoolean("demoOpen") ?? false);
        RefreshMap();
        if (File.Exists(LayerPath))
            _ = LoadSavedLayer();
    }

    private void BuildDemoPanel(bool open)
    {
        demoToggle = AddButton("Tryb demo", ToggleDemoPanel);
        demoPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        demoPanel.SetPadding(Dp(12), Dp(4), Dp(12), Dp(12));
        demoPanel.Background = RoundedBackground(Color.Rgb(235, 241, 247));
        panel.AddView(demoPanel);

        AddHeading("Scenariusz ćwiczenia", 18, demoPanel);
        var positionSwitch = new Switch(this)
        {
            Text = "Potwierdzenie bez satelitów · symulacja",
            Checked = useExercisePosition,
        };
        positionSwitch.SetMinHeight(Dp(48));
        positionSwitch.CheckedChange += (_, change) =>
        {
            useExercisePosition = change.IsChecked;
            awaitingPosition = false;
            confirmationMessage = null;
            RefreshMap();
        };
        demoPanel.AddView(positionSwitch);
        AddButton("Symuluj alert EWSS", ReceiveEwssAlert, demoPanel);
        nextStage = AddButton("", AdvanceStage, demoPanel);
        AddHeading("Rozkład tras", 16, demoPanel);
        distributionStatus = AddText("", 14, demoPanel);

        AddHeading("Diagnostyka zgłoszeń", 18, demoPanel);
        AddText("OSNMA: symulacja · EGNOS: brak potwierdzenia korekt", 14, demoPanel);
        gnssStatus = AddText("Odczyt Galileo jest wyłączony.", 14, demoPanel);
        reader = new GalileoReader(this, ShowGnssStatus);
        AddButton("Sprawdź odbiornik GNSS", EnableGnss, demoPanel);
        exitStatus = AddText("", 14, demoPanel);
        RefreshExitStatus();
        AddButton("Pokaż JSON ostatniego zgłoszenia", ShowLastExitReport, demoPanel);

        AddHeading("Archiwalne warstwy zalania", 18, demoPanel);
        layerStatus = AddText("Brak zapisanej warstwy Copernicus EMS.", 14, demoPanel);
        AddButton("Wczytaj lokalny GeoJSON Copernicus EMS", PickLayer, demoPanel);
        viewSwitch = AddButton("Pokaż warstwę Copernicus", () =>
        {
            showLayer = !showLayer;
            RefreshMap();
            screen.SmoothScrollTo(0, 0);
        }, demoPanel);
        viewSwitch.Visibility = ViewStates.Gone;
        SetDemoVisibility(open);
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (monitorGnss && HasFineLocation)
            reader.Start();
    }

    protected override void OnPause()
    {
        reader.Stop();
        base.OnPause();
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        outState.PutBoolean("monitorGnss", monitorGnss);
        outState.PutBoolean("awaitingPosition", awaitingPosition);
        outState.PutBoolean("useExercisePosition", useExercisePosition);
        outState.PutBoolean("demoOpen", demoPanel.Visibility == ViewStates.Visible);
        outState.PutString("ewssBanner", ewssBanner.Visibility == ViewStates.Visible ? ewssBanner.Text : null);
    }

    private void ToggleDemoPanel()
    {
        var open = demoPanel.Visibility != ViewStates.Visible;
        SetDemoVisibility(open);
        if (open)
            screen.Post(() => screen.SmoothScrollTo(0, demoToggle.Top));
        else if (showLayer)
        {
            showLayer = false;
            RefreshMap();
            screen.SmoothScrollTo(0, 0);
        }
    }

    private void SetDemoVisibility(bool open)
    {
        demoPanel.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        demoToggle.Text = open ? "Zamknij tryb demo" : "Tryb demo";
    }

    private void RunPrimaryAction()
    {
        switch (action)
        {
            case ResidentAction.FindRoute:
                selected = OfflineScenario.Draw(OfflineScenario.Distribution(StartNode, OfflineScenario.Closures(stage), stage),
                    Random.Shared);
                routeWithdrawn = false;
                SaveExerciseRoute();
                RefreshMap();
                break;
            case ResidentAction.ConfirmExit:
                ConfirmExit();
                break;
            case ResidentAction.ReturnToRoute:
                showLayer = false;
                RefreshMap();
                break;
        }
    }

    private void AdvanceStage()
    {
        stage = (stage + 1) % 3;
        routeWithdrawn = stage != 0 && (selected is not null || routeWithdrawn);
        selected = null;
        savedExitMode = null;
        confirmationMessage = null;
        awaitingPosition = false;
        showLayer = false;
        ewssBanner.Visibility = ViewStates.Gone;
        SaveExerciseRoute();
        RefreshMap();
        screen.SmoothScrollTo(0, 0);
    }

    private bool HasFineLocation => CheckSelfPermission(Manifest.Permission.AccessFineLocation) == Permission.Granted;

    private void EnableGnss()
    {
        if (HasFineLocation)
        {
            monitorGnss = true;
            reader.Start();
        }
        else
        {
            RequestPermissions([Manifest.Permission.AccessFineLocation, Manifest.Permission.AccessCoarseLocation],
                LocationRequest);
        }
    }

    private void SaveExerciseRoute()
    {
        using var editor = RoutePreferences.Edit()!;
        editor.PutInt("stage", stage);
        editor.PutString("exit", selected?.Route.Exit);
        editor.PutString("savedExitMode", savedExitMode);
        editor.PutBoolean("routeWithdrawn", routeWithdrawn);
        editor.Apply();
    }

    private void ReceiveEwssAlert()
    {
        var alert = ewss.Receive();
        ewssBanner.Text = $"{alert.Level}{(alert.Simulated ? " · EWSS (symulacja)" : "")}\n{alert.Text}";
        ewssBanner.Visibility = ViewStates.Visible;
        showLayer = false;
        RefreshMap();
        screen.SmoothScrollTo(0, 0);
    }

    private void ConfirmExit()
    {
        if (useExercisePosition && selected is { } route)
        {
            var exit = OfflineScenario.Nodes[route.Route.Exit];
            SaveExit(new GnssPosition(exit.Latitude, exit.Longitude, 8, DateTimeOffset.UtcNow, GalileoUsed: false),
                simulated: true);
            return;
        }

        if (reader.CurrentPosition() is { } position)
        {
            SaveExit(position, simulated: false);
            return;
        }

        awaitingPosition = true;
        if (!HasFineLocation)
        {
            confirmationMessage = "Zezwól aplikacji na dostęp do dokładnej lokalizacji.";
            RefreshMap();
        }
        EnableGnss();
    }

    private void ShowGnssStatus(string status, string? residentMessage)
    {
        gnssStatus.Text = status;
        if (awaitingPosition && residentMessage is not null && confirmationMessage != residentMessage)
        {
            confirmationMessage = residentMessage;
            RefreshMap();
        }
    }

    private void SaveExit(GnssPosition position, bool simulated)
    {
        try
        {
            var report = ExitReport.Create(position, simulated);
            exitReports.Save(report);
            RefreshExitStatus(simulated ? "Zapisano zgłoszenie testowe (symulacja)." : "Zapisano oświadczenie o wyjściu.");
            if (selected is not null && (savedExitMode is null || !simulated))
            {
                savedExitMode = report.Mode;
                SaveExerciseRoute();
            }
            awaitingPosition = false;
            confirmationMessage = null;
            RefreshMap();
            screen.SmoothScrollTo(0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            confirmationMessage = "Nie udało się zapisać zgłoszenia na urządzeniu. Spróbuj ponownie.";
            exitStatus.Text = confirmationMessage;
            RefreshMap();
        }
    }

    private void RefreshExitStatus(string? message = null)
    {
        try
        {
            var count = exitReports.PendingFiles().Length;
            var pending = $"Oczekujące lokalnie: {count}.";
            exitStatus.Text = message is null ? pending : $"{message}\n{pending}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exitStatus.Text = "Nie można odczytać kolejki lokalnej.";
        }
    }

    private void ShowLastExitReport()
    {
        try
        {
            if (exitReports.PendingFiles().FirstOrDefault() is not { } path)
            {
                exitStatus.Text = "Brak zapisanych zgłoszeń.";
                return;
            }

            var preview = new TextView(this) { Text = File.ReadAllText(path) };
            preview.SetTextIsSelectable(true);
            preview.SetPadding(Dp(16), 0, Dp(16), 0);
            var scroll = new ScrollView(this);
            scroll.AddView(preview);
            new AlertDialog.Builder(this)
                .SetTitle("Zgłoszenie lokalne · JSON")!
                .SetView(scroll)!
                .SetPositiveButton("Zamknij", (_, _) => { })!
                .Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exitStatus.Text = "Nie udało się odczytać zapisanego zgłoszenia.";
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LocationRequest)
            return;

        if (HasFineLocation)
            EnableGnss();
        else
        {
            gnssStatus.Text = "Odczyt satelitarny wymaga zgody na dokładną lokalizację.";
            if (awaitingPosition)
            {
                awaitingPosition = false;
                confirmationMessage = "Do potwierdzenia wyjścia potrzebna jest zgoda na dokładną lokalizację.";
                RefreshMap();
            }
        }
    }

    private void PickLayer()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraLocalOnly, true);
        StartActivityForResult(intent, ImportRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == ImportRequest && resultCode == Result.Ok && data?.Data is { } uri)
            _ = ImportLayer(uri);
    }

    private async Task ImportLayer(global::Android.Net.Uri uri)
    {
        try
        {
            using var stream = ContentResolver?.OpenInputStream(uri) ?? throw new IOException("Brak dostępu do pliku.");
            using var output = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory())) != 0)
            {
                if (output.Length + count > CopernicusLayer.MaxBytes)
                    throw new InvalidDataException("Plik przekracza 8 MB.");
                output.Write(buffer, 0, count);
            }

            var bytes = output.ToArray();
            var imported = await Task.Run(() => CopernicusLayer.Read(bytes));
            var temporaryPath = LayerPath + ".tmp";
            await File.WriteAllBytesAsync(temporaryPath, bytes);
            File.Move(temporaryPath, LayerPath, overwrite: true);
            SetLayer(imported, open: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException
                                       or Java.Lang.SecurityException)
        {
            layerStatus.Text = "Nie można wczytać pliku. " + ex.Message;
        }
    }

    private async Task LoadSavedLayer()
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(LayerPath);
            SetLayer(await Task.Run(() => CopernicusLayer.Read(bytes)), open: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            layerStatus.Text = "Zapisana warstwa jest niedostępna. Wczytaj ponownie plik GeoJSON.";
        }
    }

    private void SetLayer(CopernicusLayer imported, bool open)
    {
        layer = imported;
        showLayer = open;
        var observedAt = imported.ObservedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'")
            ?? "czas obserwacji nieznany";
        layerStatus.Text = $"Import lokalny: {imported.Product ?? "brak ID produktu"} • {observedAt}";
        viewSwitch.Visibility = ViewStates.Visible;
        RefreshMap();
    }

    private void RefreshMap()
    {
        var closed = OfflineScenario.Closures(stage);
        var options = OfflineScenario.Distribution(StartNode, closed, stage);
        var archive = showLayer ? layer : null;
        if (archive is not null)
        {
            map.DisplayLayer(archive);
            mapLegend.Text = "Niebieski: archiwalny zasięg zalania";
        }
        else
        {
            map.DisplayDemo(StartNode, closed, selected?.Route);
            mapLegend.Text = "Niebieski: zalanie · zielony: trasa · czerwony: zamknięcie";
        }

        action = ResidentFlow.NextAction(archive is not null, selected is not null, options.Count > 0,
            savedExitMode is not null);
        RenderResidentActions();

        distributionStatus.Text = options.Count == 0 ? "Brak dostępnych tras." : string.Join("\n", options.Select(option =>
            $"{ExitName(option.Route.Exit)} • {option.DepartsAt:HH:mm}–{option.ArrivesAt:HH:mm} UTC • {option.Weight:P0}"));
        nextStage.Text = stage switch
        {
            0 => "Zamknij most w scenariuszu",
            1 => "Zamknij północny odcinek",
            _ => "Uruchom demo od początku"
        };
        viewSwitch.Text = archive is null ? "Pokaż warstwę Copernicus" : "Wróć do mapy ćwiczenia";
    }

    private void RenderResidentActions()
    {
        var route = selected is null ? "" : $"{ExitName(selected.Route.Exit)} · start {selected.DepartsAt:HH:mm} UTC";
        var (heading, detail, button) = action switch
        {
            ResidentAction.FindRoute => ("Trasa ewakuacji",
                routeWithdrawn ? "Poprzednia trasa została wycofana. Wyznacz nową." : "",
                "Wyznacz trasę ewakuacji"),
            ResidentAction.ConfirmExit => (route, confirmationMessage ?? (useExercisePosition
                    ? "Symulacja · zgłoszenie użyje pozycji z ćwiczenia."
                    : "Potwierdź wyjście po opuszczeniu strefy."),
                "Potwierdź wyjście"),
            ResidentAction.ExitSaved => (savedExitMode == "simulated" ? "Zgłoszenie testowe · symulacja" : "Zgłoszenie wyjścia",
                "Zapisano na tym urządzeniu · oczekuje na wysłanie.", ""),
            ResidentAction.ReturnToRoute => ("Archiwalne zalanie", "Mapa archiwalna Copernicus EMS.",
                "Wróć do mapy ćwiczenia"),
            _ => ("Brak dostępnej trasy", routeWithdrawn ? "Poprzednia trasa została wycofana." : "", ""),
        };
        routeStatus.Text = heading;
        residentStatus.Text = detail;
        residentStatus.Visibility = detail.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        primaryAction.Text = button;
        primaryAction.Visibility = button.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private string StartNode => OfflineScenario.StartNode(stage);

    private static string ExitName(string exit) => exit == "exit-east" ? "Wyjście wschodnie" : "Wyjście północne";

    private TextView AddHeading(string text, int size, LinearLayout? parent = null)
    {
        var label = AddText(text, size, parent);
        label.SetTypeface(Typeface.DefaultBold, global::Android.Graphics.TypefaceStyle.Bold);
        label.SetTextColor(Color.Rgb(21, 45, 67));
        return label;
    }

    private TextView AddText(string text, int size, LinearLayout? parent = null)
    {
        var label = new TextView(this) { Text = text, TextSize = size };
        (parent ?? panel).AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        return label;
    }

    private Button AddButton(string text, Action action, LinearLayout? parent = null)
    {
        var button = new Button(this) { Text = text };
        button.SetAllCaps(false);
        button.Click += (_, _) => action();
        (parent ?? panel).AddView(button);
        return button;
    }

    private GradientDrawable RoundedBackground(Color color)
    {
        var background = new GradientDrawable();
        background.SetColor(color);
        background.SetCornerRadius(Dp(12));
        return background;
    }

    private int Dp(int value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1));

    private sealed class SafeScreenInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            var safeArea = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            view.SetPadding(safeArea.Left, safeArea.Top, safeArea.Right, safeArea.Bottom);
            return insets;
        }
    }
}
