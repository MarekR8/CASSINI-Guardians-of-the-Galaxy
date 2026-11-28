using Android.Content;
using Android.Locations;
using Android.OS;

namespace Cassini.Offline;

internal sealed class GalileoReader(Context context, Action<string> show) : Java.Lang.Object, ILocationListener
{
    private const long Second = 1_000_000_000;
    private readonly LocationManager? manager = context.GetSystemService(Context.LocationService) as LocationManager;
    private SatelliteStatus? callback;
    private Location? lastLocation;
    private long statusObservedAt;
    private bool galileoOnly;

    internal void Start()
    {
        if (callback is not null)
            return;

        if (manager is null || !manager.IsProviderEnabled(LocationManager.GpsProvider))
        {
            show("Odbiornik GNSS jest wyłączony lub niedostępny.");
            return;
        }

        var executor = context.MainExecutor;
        if (executor is null)
        {
            show("Nie można uruchomić odczytu GNSS.");
            return;
        }

        try
        {
            callback = new SatelliteStatus(OnSatellites);
            if (!manager.RegisterGnssStatusCallback(executor, callback))
            {
                Stop();
                show("Odbiornik nie udostępnia danych o konstelacji.");
                return;
            }

            manager.RequestLocationUpdates(LocationManager.GpsProvider, 1000, 0, this);
            show("Oczekiwanie na pomiar i status satelitów Galileo.");
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.IllegalArgumentException)
        {
            Stop();
            show("Nie można uruchomić odbiornika GNSS.");
        }
    }

    internal void Stop()
    {
        if (callback is null)
            return;

        manager?.RemoveUpdates(this);
        manager?.UnregisterGnssStatusCallback(callback);
        callback = null;
        lastLocation = null;
        statusObservedAt = 0;
        galileoOnly = false;
        show("Odczyt Galileo jest wstrzymany.");
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

        galileoOnly = galileo && !otherConstellation;
        statusObservedAt = SystemClock.ElapsedRealtimeNanos();
        if (!galileoOnly)
        {
            show(galileo
                ? "Odbiornik raportuje pomiar mieszany z Galileo. Demo pomija jego współrzędne."
                : "Brak potwierdzenia udziału Galileo w ostatnim pomiarze odbiornika.");
            return;
        }

        if (!ShowMeasurement())
            show("Odbiornik raportuje Galileo. Oczekiwanie na odpowiadający pomiar pozycji.");
    }

    public void OnLocationChanged(Location location)
    {
        if (callback is null)
            return;

        var mock = OperatingSystem.IsAndroidVersionAtLeast(31) ? location.Mock : location.IsFromMockProvider;
        lastLocation = location.Provider == LocationManager.GpsProvider && !mock ? location : null;
        if (lastLocation is null)
            show("Pominięto pozycję ze źródła innego niż sprzętowy GNSS.");
        else
            ShowMeasurement();
    }

    private bool ShowMeasurement()
    {
        if (CurrentMeasurement() is not { } location)
            return false;

        var time = DateTimeOffset.FromUnixTimeMilliseconds(location.Time);
        var accuracy = location.HasAccuracy ? $" • dokładność ±{location.Accuracy:F0} m" : "";
        show($"GNSS: {location.Latitude:F5}, {location.Longitude:F5}{accuracy}\n" +
             $"{time:HH:mm:ss} UTC • odbiornik raportuje tylko Galileo");
        return true;
    }

    internal GalileoPosition? CurrentPosition()
    {
        if (CurrentMeasurement() is not { HasAccuracy: true } location)
            return null;

        return new GalileoPosition(location.Latitude, location.Longitude, location.Accuracy,
            DateTimeOffset.FromUnixTimeMilliseconds(location.Time));
    }

    private Location? CurrentMeasurement() =>
        galileoOnly && lastLocation is { } location &&
        Math.Abs(location.ElapsedRealtimeNanos - statusObservedAt) <= Second &&
        SystemClock.ElapsedRealtimeNanos() - location.ElapsedRealtimeNanos <= Second * 2 ? location : null;

    public void OnProviderDisabled(string provider)
    {
        lastLocation = null;
        statusObservedAt = 0;
        galileoOnly = false;
        show("Odbiornik GNSS jest wyłączony.");
    }

    public void OnProviderEnabled(string provider) => show("Oczekiwanie na pomiar i status satelitów Galileo.");

    public void OnStatusChanged(string? provider, Availability status, Bundle? extras)
    {
    }

    private sealed class SatelliteStatus(Action<GnssStatus> listener) : GnssStatus.Callback
    {
        public override void OnSatelliteStatusChanged(GnssStatus status) => listener(status);
    }
}
