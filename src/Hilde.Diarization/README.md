# Hilde.Diarization – Phase 3

Sprechertrennung mit sherpa-onnx (Segmentierung + Sprecher-Embeddings + Clustering), Implementierung von
`IDiarizationEngine` aus `Hilde.Core`. Läuft als dritte Engine-Art (`--kind diarization`) im isolierten
Prozess `Hilde.Engine.exe`. Sprecher werden neutral als „Sprecher 1/2/…“ geführt, Überlappungen als
unsicher markiert. Diarisierung ≠ Sprecheridentifikation.
