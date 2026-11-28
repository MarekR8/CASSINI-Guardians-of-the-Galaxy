using Android.Content;
using Android.Locations;
using Android.OS;

namespace Cassini.Offline;

internal sealed class GalileoReader(Context context, Action<string, string?> show) : Java.Lang.Object, ILocationListener
{
    private const string Waiting = "Szukamy pozycji satelitarnej. Na otwartej przestrzeni telefon zwykle ustali ją szybciej.";
    private const string Disabled = "Włącz lokalizację w ustawieniach telefonu i spróbuj ponownie.";
    private readonly LocationManager? manager = context.GetSystemService(Context.LocationService) as LocationManager;
    private SatelliteStatus? callback;
    private bool listening;
    private Location? lastLocation;
    private long locationReceivedAt;
    private long statusObservedAt;
    private bool galileoUsed;
    private bool otherConstellationUsed;

    internal void Start()
    {
        if (manager is null || !manager.IsProviderEnabled(LocationManager.GpsProvider))
        {
            show("Odbiornik GNSS jest wyłączony lub niedostępny.", Disabled);
            return;
        }

        if (listening)
        {
            ShowMeasurement();
            return;
        }

        var executor = context.MainExecutor;
        if (executor is null)
        {
            show("Nie można uruchomić odczytu GNSS.", "Odczyt pozycji jest niedostępny. Spróbuj ponownie.");
            return;
        }

        try
        {
            callback = new SatelliteStatus(OnSatellites);
            if (!manager.RegisterGnssStatusCallback(executor, callback))
            {
                callback.Dispose();
                callback = null;
            }

            manager.RequestLocationUpdates(LocationManager.GpsProvider, 1000, 0, this);
            listening = true;
            show("Oczekiwanie na pomiar GNSS.", Waiting);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            Stop();
            show("Nie można uruchomić odbiornika GNSS.", "Odczyt pozycji jest niedostępny. Spróbuj ponownie.");
        }
    }

    internal void Stop()
    {
        if (listening)
            manager?.RemoveUpdates(this);
        listening = false;
        if (callback is not null)
        {
            manager?.UnregisterGnssStatusCallback(callback);
            callback.Dispose();
        }
        callback = null;
        ClearMeasurement();
        show("Odczyt GNSS jest wstrzymany.", null);
    }

    private void OnSatellites(GnssStatus satellites)
    {
        if (callback is null)
            return;

        var galileo = false;
        var otherConstellation = false;
        for (var index = 0; index < satellites.SatelliteCount; index++)
        {
            if (!satellites.UsedInFix(index))
                continue;

            if (satellites.GetConstellationType(index) == GnssConstellationType.Galileo)
                galileo = true;
            else
                otherConstellation = true;
        }

        galileoUsed = galileo;
        otherConstellationUsed = otherConstellation;
        statusObservedAt = SystemClock.ElapsedRealtimeNanos();
        ShowMeasurement();
    }

    public void OnLocationChanged(Location location)
    {
        if (!listening)
            return;

        var mock = OperatingSystem.IsAndroidVersionAtLeast(31) ? location.Mock : location.IsFromMockProvider;
        lastLocation = location.Provider == LocationManager.GpsProvider && !mock ? location : null;
        locationReceivedAt = SystemClock.ElapsedRealtimeNanos();
        if (lastLocation is null)
            show("Pominięto pozycję testową lub ze źródła innego niż GNSS.", Waiting);
        else
            ShowMeasurement();
    }

    private void ShowMeasurement()
    {
        if (CurrentPosition() is not { } position)
        {
            show("Oczekiwanie na świeży pomiar GNSS z informacją o dokładności.", Waiting);
            return;
        }

        var evidence = position.GalileoUsed
            ? otherConstellationUsed ? "Galileo i inne konstelacje" : "Galileo"
            : "udział Galileo niepotwierdzony";
        show($"GNSS: {position.Latitude:F5}, {position.Longitude:F5} • dokładność ±{position.AccuracyMeters:F0} m\n" +
             $"{position.ObservedAt:HH:mm:ss} UTC • {evidence}",
             "Pozycja gotowa. Jeśli jesteś już poza strefą, potwierdź wyjście.");
    }

    internal GnssPosition? CurrentPosition()
    {
        if (lastLocation is not { HasAccuracy: true } location ||
            !float.IsFinite(location.Accuracy) || location.Accuracy < 0)
            return null;

        var now = SystemClock.ElapsedRealtimeNanos();
        var elapsed = location.ElapsedRealtimeNanos;
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        // Only a valid boot-clock timestamp can be matched with satellite status.
        var hasMonotonicTimestamp = elapsed > 0 && elapsed <= now;
        var fresh = hasMonotonicTimestamp ? GnssPosition.IsFresh(elapsed, now)
            : GnssPosition.IsFresh(locationReceivedAt, now) && GnssPosition.IsFreshUtc(observedAt, DateTimeOffset.UtcNow);
        if (!fresh)
            return null;

        return new GnssPosition(location.Latitude, location.Longitude, location.Accuracy,
            observedAt, hasMonotonicTimestamp && GnssPosition.MatchesGalileoStatus(elapsed, statusObservedAt, galileoUsed));
    }

    private void ClearMeasurement()
    {
        lastLocation = null;
        locationReceivedAt = 0;
        statusObservedAt = 0;
        galileoUsed = false;
        otherConstellationUsed = false;
    }

    public void OnProviderDisabled(string provider)
    {
        ClearMeasurement();
        show("Odbiornik GNSS jest wyłączony.", Disabled);
    }

    public void OnProviderEnabled(string provider) => show("Oczekiwanie na pomiar GNSS.", Waiting);

    public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
    {
    }

    private sealed class SatelliteStatus(Action<GnssStatus> listener) : GnssStatus.Callback
    {
        public override void OnSatelliteStatusChanged(GnssStatus status) => listener(status);
    }
}
