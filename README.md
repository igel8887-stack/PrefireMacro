# Prefire

Windows-App mit lokaler HUD-Erkennung, einstellbarer Baufolge und lokaler Diagnoseaufnahme.

## Starten

Einmal [PrefireLoader.exe](https://github.com/igel8887-stack/PrefireMacro/releases/latest/download/PrefireLoader.exe) herunterladen. Alternativ gibt es die [kleine ZIP-Datei](https://github.com/igel8887-stack/PrefireMacro/releases/latest/download/PrefireLoader.zip).

Beim Oeffnen prueft der Loader **GitHub Releases** auf die neueste stabile Prefire-Version. Signatur, Dateigroesse und SHA-256 werden vor Verwendung geprueft. Dann auf **Prefire starten** klicken; die App startet pausiert. Ohne Netz bleibt eine gepruefte lokale bzw. mitgelieferte Version verfuegbar. Einstellungen und Videos bleiben auf dem eigenen PC. Farben und Tasten richtet jeder selbst ein.

## Aenderungen veroeffentlichen

Dateien auf `main` bearbeiten und committen. Unter **Actions** den Lauf von „Prefire - Build, Test und Release“ verfolgen. Nur wenn App-Build, Selbsttest, Loader-Build und Loader-Selbsttest bestehen, wird ein vollstaendiges GitHub Release veroeffentlicht. Eine numerische Buildversion wird automatisch vergeben. Der vorhandene GitHub-Loader findet es beim naechsten Oeffnen.

Aenderungen auf anderen Branches gehen nicht automatisch an alle Nutzer. Fremde Pull Requests erhalten den Signierschluessel nicht.

Die Vercel-Loaderdatei muss **einmal** gegen den GitHub-Loader ausgetauscht werden. Danach bleibt dieselbe GitHub-Loaderdatei verwendbar; auch Loader-Updates werden signiert geladen.

## Turbomodus

Unter **Einrichten → Turbomodus** einschalten und speichern. Turbo nutzt mehr parallele CPU-Arbeit, fordert haeufiger neue Verarbeitung an und kann dieselbe bereits eingerichtete GPU-Quelle auch fuer die abschliessende Pruefung verwenden. Er startet bei alten Konfigurationen ausgeschaltet. Details, Messungen und verbleibende Grenzen stehen in [docs/Turbo.md](docs/Turbo.md).

## Lokal entwickeln

`build.ps1` baut die App mit dem Windows-.NET-Framework-Compiler. `ci/Test-Prefire.ps1` baut und prueft sie mit simulierten Eingaben. Der Launcher-Quellcode liegt in `Loader/PrefireLoader.cs`.

Das Signieren ist Herausgeberarbeit. Im Repository liegt nur `Loader/GitHubReleasePublicKey.xml`. Der zugehoerige private GitHub-Freigabeschluessel wird separat als Actions Secret `PREFIRE_SIGNING_KEY` verwaltet; er gehoert nie in Commits, Release-Dateien oder die Loaderdatei. Der bisherige Vercel-Schluessel wird dafuer nicht verwendet.

Tests sind keine Bestaetigung der Schussreaktionszeit oder Platzierung im Spiel.
