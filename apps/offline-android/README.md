# CASSINI Offline dla Androida

Samodzielne demo dla Androida 11 lub nowszego. Aplikacja ma własną mapę, testową sieć pieszą, rozkład tras i losowanie w węźle. Działa w trybie samolotowym z wyłączonym Wi-Fi. Nie potrzebuje uruchomionych mikroserwisów. Dane scenariusza są syntetyczne.

1. Otwórz aplikację i wybierz **Wylosuj trasę w węźle**. Widać rozkład, prawdopodobieństwa i wynik losowania.
2. Zamknij most i ponów losowanie. Jedyna pozostała trasa wiedzie na północ. Po zamknięciu także tego odcinka losowanie staje się niedostępne.
3. Na urządzeniu obsługującym Galileo włącz lokalizację i wybierz **Sprawdź odbiornik Galileo**. Aplikacja korzysta z natywnego GNSS. Pokazuje współrzędne po skorelowaniu świeżego pomiaru ze statusem, w którym odbiornik zgłosił użycie wyłącznie Galileo. Pomija pomiary mieszane, symulowane i bez potwierdzenia. To odczyt informacyjny, bez wpływu na trasy testowe. Aplikacja nie zbiera dowodów użycia EGNOS. [Galileo Open Service](https://www.euspa.europa.eu/galileo-os) · [Android GnssStatus](https://developer.android.com/reference/android/location/GnssStatus)
4. Opcjonalnie wczytaj wcześniej pobraną warstwę Copernicus EMS jako lokalny GeoJSON `FeatureCollection` z geometriami `Polygon` lub `MultiPolygon` w WGS 84. W polach głównych `productId` i `observedAt` możesz podać ID produktu i czas obserwacji w ISO 8601. Bez tych danych widok informuje o brakach. Import jest archiwalnym podglądem warstwy i nie zmienia testowego grafu. [Pobieranie danych Copernicus EMS](https://mapping.emergency.copernicus.eu/about/rapid-mapping-manual/product-overview/how-is-a-product-delivered/viewing-and-downloading-a-product/)
5. W sekcji **Potwierdzenie wyjścia** zapisz oświadczenie ze świeżą pozycją Galileo albo wybierz **Zapisz zgłoszenie testowe · symulacja**, aby przejść scenariusz bez odbiornika. Raport zawiera współrzędne, czas UTC i oszacowaną dokładność w metrach. OSNMA jest oznaczone jako symulacja, a EGNOS pozostaje bez potwierdzenia. **Pokaż JSON ostatniego zgłoszenia** otwiera podgląd danych zapisanych w prywatnym katalogu aplikacji. Licznik wskazuje raporty oczekujące lokalnie.
6. Po wylosowaniu trasy wybierz **Odbierz alert EWSS · symulacja**. Aplikacja pokazuje ostrzeżenie i wraca do zapisanej trasy. Zmiana etapu ćwiczenia, np. zamknięcie mostu, usuwa poprzednie losowanie. Po ponownym uruchomieniu aplikacja przywraca ostatni etap i wybraną trasę, jeśli nadal jest dostępna w tym etapie.

Do kompilacji potrzebujesz .NET 10 z workloadem `android`, Android SDK z API 36 i JDK. Z katalogu głównego repozytorium uruchom:

```sh
dotnet build apps/offline-android/Cassini.Offline.Android.csproj -c Release
dotnet run --project apps/offline-android/tests/Offline.Tests.csproj
```

Jeśli środowisko nie wykrywa SDK lub JDK, podaj `-p:AndroidSdkDirectory=...` i `-p:JavaSdkDirectory=...`. Podpisany plik demo znajduje się w `apps/offline-android/bin/Release/net10.0-android/org.cassini.guardians.offline-Signed.apk`. APK nie deklaruje uprawnienia `INTERNET`. Pierwsza kompilacja może wymagać pobrania zależności narzędzi. Po zainstalowaniu nie są potrzebne.

GOVSATCOM i IRIS² wymagają odpowiedniego łącza i wyposażenia punktu koordynacji. Aplikacja samodzielna nie przesyła zgłoszeń ani tras pomiędzy urządzeniami. [GOVSATCOM Hub](https://www.euspa.europa.eu/govsatcom-hub) · [IRIS²](https://defence-industry-space.ec.europa.eu/eu-space/iris2-secure-connectivity_en)
