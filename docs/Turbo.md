# Turbomodus (V27.5)

Unter **Einrichten → Turbomodus** einschalten, speichern und mit F8 aktivieren. Eine Änderung pausiert die App. Alte Konfigurationen starten mit ausgeschaltetem Turbo.

Turbo nutzt für große Erkennungsbereiche bis zu acht statt vier CPU-Arbeiter. Während die Haltetaste gedrückt ist, fordert ein separater 1-ms-Timer Verarbeitung auf dem UI-Thread an. Höchstens eine Anforderung ist vorgemerkt. Die tatsächliche Bildrate ist durch Aufnahme und Verarbeitung begrenzt; die App verspricht keine 1000 Bilder pro Sekunde.

Zielbeobachtung und letzte Schussprüfung verwenden getrennte Bitmaps und dieselbe synchronisierte GPU-Quelle. Der D3D-Kontext wird immer nur von einem Thread gleichzeitig verwendet ([Microsoft: Multithreading in Direct3D 11](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-render-multi-thread-intro)). Es gibt keinen Puffer, der alte Bilder als neue Prüfung ausgeben kann.

Die letzte Prüfung darf keine GPU-Quelle initialisieren. Sie verlangt eine Präsentation nach Ablauf des Verlustfilters und wartet höchstens 3 ms auf ein frisches GPU-Bild. Andernfalls wird frisch über GDI aufgenommen. Ein GPU-Fehler schaltet diesen Weg bis zum nächsten Fokus- oder Optionswechsel ab; Fehlercode und Schritt stehen im Protokoll.

Die App behält Verlustfilter, unabhängige Bestätigung, Fokus-, Haltetasten- und Aktualitätsprüfungen bei. Turbo kann CPU/GPU stärker beanspruchen und mit einem bereits ausgelasteten Spiel konkurrieren.

## Weitere Korrekturen

- Die obere Zielzone deckt die in der V27.4-Aufnahme verworfenen Deckenbalken bei y=363–413 auf einem 1440 Pixel hohen Spielbereich ab. Seitliche und sehr weit oberhalb liegende Anzeigen bleiben ausgeschlossen. Vor einem Schuss ist weiterhin ein stabil bestätigtes Ziel erforderlich.
- Ein fehlendes Ziel wird im Protokoll als fehlendes Ziel gemeldet; bislang erschien dafür irreführend „Balken zu weit vom Fadenkreuz“.
- Die Versionsbezeichnung hat keinen Test-Zusatz.

## Nachweise und Grenzen

Der Selbsttest prüft unter anderem gemeinsame GPU-Nutzung, serialisierten Kontextzugriff, Initialisierungsverbot in der letzten Prüfung, Fehlerabschaltung, Rücksetzung und Konfigurationskompatibilität. Die 19 vorhandenen Offline-Prüfgruppen bestehen im Normal- und Turbomodus.

Ein abwechselnder Vergleich an acht großen gespeicherten Bildern (je sechs Messdurchläufe nach Aufwärmen) ergab für die reine CPU-Erkennung Median 10,136 ms normal und 7,273 ms Turbo. Die Ergebnisse stimmten überein. Das ist kein Nachweis der gesamten Schussreaktion oder eines GPU-Geschwindigkeitsgewinns im Spiel.

Ein offener Edit-Ausschnitt bei weiterhin sichtbarem Lebensbalken ist mit der Balkenerkennung nicht zuverlässig als freie Schussbahn erkennbar. Auch Turbo löst dieses geometrische Problem nicht. Gesendete Bau- und Schusseingaben bestätigen keine erfolgreiche Aktion auf dem Spielserver.
