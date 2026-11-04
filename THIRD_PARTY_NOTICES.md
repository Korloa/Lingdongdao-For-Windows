# Third-party notices

The on-demand HTML animation panel uses Microsoft.Web.WebView2 1.0.4314.35.
Its SDK license and third-party notices are included in
`third_party/webview2/LICENSE.txt` and `third_party/webview2/NOTICE.txt`.
The Microsoft Edge WebView2 Runtime is installed separately on Windows.

Optional desktop perception uses RapidOCR (Apache-2.0 model derivatives and
project license; see https://rapidai.github.io/RapidOCRDocs/main/en/model_licenses/),
ONNX Runtime (MIT; https://github.com/microsoft/onnxruntime), and an optional
Salesforce GPA-GUI-Detector model (MIT; https://huggingface.co/Salesforce/GPA-GUI-Detector).
Model weights are downloaded separately and are not committed to this source tree.

The NetEase Cloud Music process signature and playback structure offsets used by
`NetEasePlaybackReader.cs`, and the KuGou timeline scanning approach used by
`KugouProgressReader.cs`, were adapted from MuSync:
https://github.com/ZhaoBuyan/MuSync

MIT License

Copyright (c) 2026 ZhaoBuyan

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The KuGou background control button mapping was informed by the experimental
KugouControlPoc project (no source code from that project is included):
https://github.com/Enkianssus/AwooMusicBot/tree/main/experiments/kugou-control-poc

Audio session inspection uses NAudio.Wasapi 3.3.0:
https://github.com/naudio/NAudio

Offline microphone capture also uses NAudio.WinMM 3.3.0 under the same MIT license.

Copyright 2008-2026 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Offline recognition uses Vosk 0.3.38 and vosk-model-small-cn-0.22 under Apache License 2.0.
Upstream: https://github.com/alphacep/vosk-api and https://alphacephei.com/vosk/models
The complete Apache 2.0 text is included in third_party/vosk/LICENSE.txt.
