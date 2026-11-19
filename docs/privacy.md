# Datenschutz

- **Lokal:** Aufnahmen, Transkripte, Wörterbuch, Einstellungen und Modelle liegen unter `%LOCALAPPDATA%\HILDE\`.
- **Kein Netz im Betrieb:** keine Cloud, keine Telemetrie, keine Anmeldung, keine API-Schlüssel. Netzwerk nur für den
  ausdrücklich gestarteten Modell-Download (Hugging Face, GitHub) – alternativ Offline-Paket.
- **Oberfläche:** WebView2 lädt nur lokale Dateien (virtuelle Domain `app.hilde.example`, CSP `connect-src 'none'`),
  externe Navigation und neue Fenster sind gesperrt.
- **Protokolle:** nur Kennzahlen (Dauer, Modus, Fehler-IDs), **nie** Diktat- oder Transkriptinhalte, keine Tasten.
- **Tastatur-Hook:** wertet nur die Diktiertaste und Esc aus; nichts wird gespeichert.
- **Zwischenablage:** Diktate werden mit „nicht in den Verlauf/die Cloud“ markiert; der vorherige Inhalt wird nur
  zurückgeschrieben, wenn ihn zwischenzeitlich niemand geändert hat.
- **Löschen:** Diktate einzeln vollständig löschbar (Text + Audio). Audio wird standardmäßig nach erfolgreicher
  Verarbeitung gelöscht. Archiv abschaltbar. Deinstallation entfernt Programmdateien, nicht die Benutzerdaten.
- **Meetings (Phase 3):** Aufnahme nur sichtbar und mit Hinweis auf Einwilligung aller Beteiligten; keine
  verdeckte Aufzeichnung, keine Mitarbeiterüberwachung.
- **Sicherheit:** Pipes nur für den angemeldeten Benutzer; keine Adminrechte; erhöhte Zielprogramme werden nicht
  „umgangen“. Optionale Verschlüsselung per DPAPI ist für Phase 4 vorgesehen; BitLocker wird empfohlen.
