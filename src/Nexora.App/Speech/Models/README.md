# Meaning model

`meaning-minilm-int8.onnx` is all-MiniLM-L6-v2 (sentence-transformers, Apache License 2.0),
exported to ONNX and quantized to 8-bit with ONNX Runtime so it is about 23 MB. `meaning-vocab.txt`
is its word list. NEXORA uses it on the Scripture PC to compare what the pastor says with each verse
by meaning. It runs on the PC; nothing is sent anywhere.

The Whisper speech models are not stored here: they are 150 MB to 600 MB, so each church downloads
the one that suits its PC once (Scripture section > Listen > Set up), or imports the file, and the
Live Output PC shares it with the other PCs.
