# Dane dostarczane przez zespół

- [Walencja · mapa, dane EMSR773 i FlyPix](valencia.md)
- [Walencja · paczka grafu i kontrakt dla etapu 03](valencia-task03.md)
- [Szablon produktu dla lokalnego ćwiczenia](product.example.json)

Surowe dane, stan panelu i poświadczenia trzymaj w ignorowanych katalogach lokalnych.

Szablon produktu obejmuje nieuzupełnione identyfikatory źródeł, czasy, eksporty oraz format potwierdzenia sprzętu i łącza. Nie jest konfiguracją runtime i nie zawiera sekretów. Dla dostępu do rzeczywistego ORS użyj `Integrations__Ors__ApiKey` albo .NET user-secrets. Pozostałe dane logowania i zatwierdzone kontrakty dostawców trzymaj poza Git; nie wymyślaj kluczy lub adresów API. Zobacz [macierz integracji](../../docs/tools/README.md).
