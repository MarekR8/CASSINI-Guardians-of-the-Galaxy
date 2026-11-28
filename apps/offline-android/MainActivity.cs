using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

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
    private TextView ewssBanner = null!;
    private TextView routeStatus = null!;
    private TextView distributionStatus = null!;
    private TextView gnssStatus = null!;
    private TextView layerStatus = null!;
    private TextView exitStatus = null!;
    private LocalExitReports exitReports = null!;
    private DemoMapView map = null!;
    private GalileoReader reader = null!;
    private Button nextStage = null!;
    private Button drawRoute = null!;
    private Button viewSwitch = null!;
    private CopernicusLayer? layer;
    private bool showLayer;
    private bool monitorGnss;
    private int stage;
    private ScheduledWalkingRoute? selected;
    private ISharedPreferences RoutePreferences => GetSharedPreferences("exercise-route", FileCreationMode.Private)!;
    private string LayerPath => Path.Combine(FilesDir!.AbsolutePath, "copernicus.geojson");

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        stage = RoutePreferences.GetInt("stage", 0);
        selected = OfflineScenario.RestoreRoute(stage, RoutePreferences.GetString("exit", null));
        monitorGnss = state?.GetBoolean("monitorGnss") ?? false;

        screen = new ScrollView(this);
        screen.SetOnApplyWindowInsetsListener(new SafeScreenInsets());
        panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetPadding(Dp(20), Dp(20), Dp(20), Dp(20));
        screen.AddView(panel);
        SetContentView(screen);

        AddText("CASSINI Offline", 32);
        AddText("Demo ewakuacji pieszej", 20);
        AddText("Scenariusz zawiera dane testowe. Obsługa mapy i obliczanie tras działają bez sieci.", 15);
        ewssBanner = AddText("", 17);
        ewssBanner.SetBackgroundColor(global::Android.Graphics.Color.Rgb(255, 234, 179));
        ewssBanner.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
        ewssBanner.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        ewssBanner.Text = state?.GetString("ewssBanner");
        ewssBanner.Visibility = string.IsNullOrEmpty(ewssBanner.Text) ? ViewStates.Gone : ViewStates.Visible;

        map = new DemoMapView(this);
        panel.AddView(map, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(290))
        {
            TopMargin = Dp(12), BottomMargin = Dp(12)
        });
        routeStatus = AddText("", 17);
        distributionStatus = AddText("", 15);
        drawRoute = AddButton("Wylosuj trasę w węźle", () =>
        {
            selected = OfflineScenario.Draw(OfflineScenario.Distribution(StartNode, OfflineScenario.Closures(stage), stage),
                Random.Shared);
            SaveExerciseRoute();
            RefreshMap();
        });
        nextStage = AddButton("", () =>
        {
            stage = (stage + 1) % 3;
            selected = null;
            ewssBanner.Visibility = ViewStates.Gone;
            SaveExerciseRoute();
            RefreshMap();
        });

        AddText("Dane satelitarne", 20);
        AddButton("Odbierz alert EWSS · symulacja", ReceiveEwssAlert);
        gnssStatus = AddText("Odczyt Galileo jest wyłączony.", 15);
        reader = new GalileoReader(this, text => gnssStatus.Text = text);
        AddButton("Sprawdź odbiornik Galileo", EnableGnss);

        AddText("Potwierdzenie wyjścia", 20);
        AddText("OSNMA: symulacja. EGNOS: brak potwierdzenia zastosowania korekt.", 15);
        exitReports = new LocalExitReports(Path.Combine(FilesDir!.AbsolutePath, "exit-reports"));
        exitStatus = AddText("", 15);
        RefreshExitStatus();
        AddButton("Potwierdź wyjście z pozycją Galileo", ConfirmExit);
        AddButton("Zapisz zgłoszenie testowe · symulacja", SaveSimulatedExit);
        AddButton("Pokaż JSON ostatniego zgłoszenia", ShowLastExitReport);

        AddText("Archiwalne warstwy zalania", 20);
        layerStatus = AddText("Brak zapisanej warstwy Copernicus EMS.", 15);
        AddButton("Wczytaj lokalny GeoJSON Copernicus EMS", PickLayer);
        viewSwitch = AddButton("Pokaż Copernicus", () =>
        {
            showLayer = !showLayer;
            RefreshMap();
        });
        viewSwitch.Visibility = ViewStates.Gone;
        RefreshMap();
        if (File.Exists(LayerPath))
            _ = LoadSavedLayer();
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
        outState.PutString("ewssBanner", ewssBanner.Visibility == ViewStates.Visible ? ewssBanner.Text : null);
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
        editor.Apply();
    }

    private void ReceiveEwssAlert()
    {
        var alert = ewss.Receive();
        var source = alert.Simulated ? "EWSS · SYMULACJA" : "EWSS";
        var route = selected is null ? "Brak zapisanej trasy." : "Otworzono zapisaną trasę ćwiczeniową.";
        ewssBanner.Text = $"{source} · {alert.Level}\n{alert.Text}\n{route}";
        ewssBanner.Visibility = ViewStates.Visible;
        showLayer = false;
        RefreshMap();
        screen.SmoothScrollTo(0, 0);
    }

    private void ConfirmExit()
    {
        if (reader.CurrentPosition() is { } position)
        {
            SaveExit(position, simulated: false);
            return;
        }

        exitStatus.Text = "Poczekaj na świeży pomiar Galileo z dokładnością i wybierz potwierdzenie ponownie.";
        EnableGnss();
    }

    private void SaveSimulatedExit()
    {
        var exit = OfflineScenario.Nodes[selected?.Route.Exit ?? OfflineScenario.Exits[0]];
        SaveExit(new GalileoPosition(exit.Latitude, exit.Longitude, 8, DateTimeOffset.UtcNow), simulated: true);
    }

    private void SaveExit(GalileoPosition position, bool simulated)
    {
        try
        {
            exitReports.Save(ExitReport.Create(position, simulated));
            RefreshExitStatus(simulated ? "Zapisano zgłoszenie testowe (symulacja)." : "Zapisano oświadczenie o wyjściu.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exitStatus.Text = "Nie udało się zapisać zgłoszenia na urządzeniu.";
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
            gnssStatus.Text = "Odczyt satelitarny wymaga zgody na dokładną lokalizację.";
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
        if (showLayer && layer is not null)
        {
            map.DisplayLayer(layer);
            routeStatus.Text = "Warstwa archiwalna. Ten widok nie wyznacza rzeczywistych tras.";
            nextStage.Visibility = ViewStates.Gone;
            drawRoute.Visibility = ViewStates.Gone;
            distributionStatus.Visibility = ViewStates.Gone;
            viewSwitch.Text = "Pokaż demo";
            return;
        }

        var closed = OfflineScenario.Closures(stage);
        var options = OfflineScenario.Distribution(StartNode, closed, stage);
        map.DisplayDemo(StartNode, closed, selected?.Route);
        routeStatus.Text = selected is not null
            ? $"Wylosowano: {ExitName(selected.Route.Exit)} • start {selected.DepartsAt:HH:mm} UTC"
            : options.Count == 0 ? "Brak dostępnej trasy w scenariuszu." : "Wybierz losowanie trasy dla bieżącego węzła.";
        distributionStatus.Text = string.Join("\n", options.Select(option =>
            $"{ExitName(option.Route.Exit)} • {option.DepartsAt:HH:mm}–{option.ArrivesAt:HH:mm} UTC • {option.Weight:P0}"));
        distributionStatus.Visibility = options.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
        drawRoute.Visibility = ViewStates.Visible;
        drawRoute.Enabled = options.Count > 0;
        nextStage.Visibility = ViewStates.Visible;
        nextStage.Text = stage switch
        {
            0 => "Zamknij most w scenariuszu",
            1 => "Zamknij północny odcinek",
            _ => "Uruchom demo od początku"
        };
        viewSwitch.Text = "Pokaż Copernicus";
    }

    private string StartNode => OfflineScenario.StartNode(stage);

    private static string ExitName(string exit) => exit == "exit-east" ? "Wyjście wschodnie" : "Wyjście północne";

    private TextView AddText(string text, int size)
    {
        var label = new TextView(this) { Text = text, TextSize = size };
        panel.AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });
        return label;
    }

    private Button AddButton(string text, Action action)
    {
        var button = new Button(this) { Text = text };
        button.Click += (_, _) => action();
        panel.AddView(button);
        return button;
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
