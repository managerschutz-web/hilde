# Freigabe-Checkliste (Pflichtenheft Kap. 10)

Interne Builds sind erlaubt. **Keine öffentliche Freigabe vor vollständiger Abnahme.**

## Vor jeder Freigabe
- [ ] `scripts/update-manifest.ps1` ausgeführt: alle `sha256` und `sizeBytes` gesetzt (CI bricht sonst ab)
- [ ] Lizenzstatus aller Modelle und Bibliotheken geprüft (`licenses/THIRD-PARTY.md`, `licenseStatus` im Manifest)
- [ ] Paketversionen in `Directory.Packages.props` festgeschrieben, CI grün
- [ ] Optional: Code-Signatur (Secrets `HILDE_SIGN_CERT_BASE64`, `HILDE_SIGN_CERT_PASSWORD`); Burn-Engine per
      `wix burn detach/reattach` mitsignieren

## Installation
- [ ] Setup auf frischem Windows 11 x64 (ohne Adminrechte) erfolgreich
- [ ] Modell-Download mit Unterbrechung und Fortsetzung
- [ ] Offline-Paket-Import
- [ ] Danach vollständig offline (Netzwerkverkehr mitschneiden)
- [ ] Deinstallation ohne Verlust von `%LOCALAPPDATA%\HILDE`

## Diktat
- [ ] Hotkey zuverlässig (auch Strg-Kürzel bleiben nutzbar), Freihandmodus, Esc
- [ ] Deutsch und Englisch
- [ ] Füllwörter und Selbstkorrekturen
- [ ] Einfügen in Word, Outlook, Edge/Chrome, Editor, Windows Terminal
- [ ] RDP/RemoteApp mit erlaubter Zwischenablage-Umleitung (Testmatrix + Einschränkungen dokumentieren)

## Daten und Stabilität
- [ ] Roh- und bereinigter Text getrennt
- [ ] Wörterbuch und Ersetzungen
- [ ] Absturz während Aufnahme (Prozess hart beenden) → Wiederherstellung beim Start
- [ ] Engine-Absturz (Hilde.Engine.exe beenden) → Diktat bleibt erhalten, Neustart
- [ ] Modelle werden nach Inaktivität freigegeben (RAM prüfen)
- [ ] 8 Stunden Dauerbetrieb ohne unbehandelten Absturz

## Messungen
- [ ] Deutscher ASR-Testsatz (`tests/AudioFixtures`), WER gemessen, Grenzwert festgelegt
- [ ] Zeit „Loslassen → Einfügen“ für CPU/GPU × Leicht/Standard
- [ ] Tastaturbedienung, Fokuszustände, Skalierung 100–200 %

## Bekannte Punkte
- WiX-Standard-Bootstrapper zeigt Englisch, sofern keine deutsche Lokalisierung hinterlegt wird.
- Nach Deinstallation kann ein Autostart-Eintrag stehen bleiben (verweist ins Leere); HILDE bereinigt ihn beim Start.
