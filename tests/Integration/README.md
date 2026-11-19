# Integrationstests

Engine-Integrationstests (Whisper/llama.cpp im isolierten Prozess) benötigen die echten Modelle und laufen
daher nicht in jeder CI-Ausführung. Ablauf (Phase 5, Freigabe):

1. Modelle mit `scripts\update-manifest.ps1 -Download` nach `%LOCALAPPDATA%\HILDE\Models` laden.
2. `tests\AudioFixtures` mit dem dokumentierten deutschen Testsatz befüllen (siehe dortige README).
3. `scripts\benchmark.ps1` misst Wortfehlerrate und Zeit „Taste loslassen → Einfügen“ für CPU/GPU × Leicht/Standard.
