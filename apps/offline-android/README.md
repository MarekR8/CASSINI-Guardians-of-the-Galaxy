# CASSINI Offline dla Androida

Samodzielne demo dla Androida 11 lub nowszego. Aplikacja ma własną mapę, testową sieć pieszą, rozkład tras i losowanie w węźle. Działa w trybie samolotowym z wyłączonym Wi-Fi. Nie potrzebuje uruchomionych mikroserwisów. Dane scenariusza są syntetyczne.

1. Wybierz **Wyznacz trasę ewakuacji**. Mapa wyróżni przydzieloną trasę i pokaże nazwę wyjścia.
2. Po opuszczeniu strefy wybierz **Potwierdź wyjście**. Aplikacja zapisze zgłoszenie ze świeżą pozycją Galileo, czasem UTC i dokładnością w metrach. Komunikat **Zapisano na tym urządzeniu · oczekuje na wysłanie** oznacza zapis lokalny.
3. Rozwiń **Tryb demo**, aby zobaczyć rozkład tras, zasymulować alert EWSS albo zamknąć most. Po zamknięciu wyznacz nową trasę prowadzącą na północ. Po zamknięciu północnego odcinka pojawi się **Brak dostępnej trasy**. Po ponownym uruchomieniu aplikacja przywraca ostatni etap i wybraną trasę, jeśli jest ona dostępna w tym etapie.
4. Do pokazu bez odbiornika wybierz **Symuluj potwierdzenie wyjścia** przy wyznaczonej trasie. Podgląd JSON i szczegóły Galileo są w diagnostyce w trybie demo. Odczyt sprzętowy wymaga świeżego pomiaru, w którym odbiornik zgłosił użycie wyłącznie Galileo. OSNMA jest oznaczone jako symulacja, a EGNOS pozostaje bez potwierdzenia. [Galileo Open Service](https://www.euspa.europa.eu/galileo-os) · [Android GnssStatus](https://developer.android.com/reference/android/location/GnssStatus)
5. Tryb demo pozwala też wczytać archiwalną warstwę Copernicus EMS z lokalnego GeoJSON `FeatureCollection` zawierającego geometrie `Polygon` lub `MultiPolygon` w WGS 84. Opcjonalne pola `productId` i `observedAt` opisują produkt i czas obserwacji. [Pobieranie danych Copernicus EMS](https://mapping.emergency.copernicus.eu/about/rapid-mapping-manual/product-overview/how-is-a-product-delivered/viewing-and-downloading-a-product/)

Do kompilacji potrzebujesz .NET 10 z workloadem `android`, Android SDK z API 36 i JDK. Z katalogu głównego repozytorium uruchom:

```sh
dotnet build apps/offline-android/Cassini.Offline.Android.csproj -c Release
dotnet run --project apps/offline-android/tests/Offline.Tests.csproj
```

Jeśli środowisko nie wykrywa SDK lub JDK, podaj `-p:AndroidSdkDirectory=...` i `-p:JavaSdkDirectory=...`. Podpisany plik demo znajduje się w `apps/offline-android/bin/Release/net10.0-android/org.cassini.guardians.offline-Signed.apk`. APK nie deklaruje uprawnienia `INTERNET`. Pierwsza kompilacja może wymagać pobrania zależności narzędzi. Po zainstalowaniu nie są potrzebne.

GOVSATCOM i IRIS² wymagają odpowiedniego łącza i wyposażenia punktu koordynacji. Aplikacja samodzielna nie przesyła zgłoszeń ani tras pomiędzy urządzeniami. [GOVSATCOM Hub](https://www.euspa.europa.eu/govsatcom-hub) · [IRIS²](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en)
