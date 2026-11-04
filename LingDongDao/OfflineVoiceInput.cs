using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Vosk;

namespace LingDongDao;

// Microphone audio stays in memory. Recognition loads only while a command is transcribed.
internal sealed class OfflineVoiceInput : IDisposable
{
    private const int SampleRate = 16000;
    private readonly MemoryStream _pcm = new();
    private readonly WaveIn _microphone;
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory,
        "models", "vosk-model-small-cn-0.22");

    public OfflineVoiceInput()
    {
        if (!File.Exists(Path.Combine(ModelPath, "conf", "model.conf")))
            throw new FileNotFoundException("缺少离线中文语音模型，请运行 scripts\\setup-voice-model.ps1 后重新启动。", ModelPath);
        _microphone = new WaveIn
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        _microphone.DataAvailable += (_, e) =>
        {
            lock (_pcm) _pcm.Write(e.Buffer, 0, e.BytesRecorded);
        };
        _microphone.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) _stopped.TrySetException(e.Exception);
            else _stopped.TrySetResult();
        };
    }

    public void Start() => _microphone.StartRecording();

    public async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken)
    {
        _microphone.StopRecording();
        await _stopped.Task.WaitAsync(cancellationToken);
        byte[] audio;
        lock (_pcm) audio = _pcm.ToArray();
        if (audio.Length < SampleRate * 2 / 2) return "";
        return await Task.Run(() => TranscribePcm(audio), cancellationToken);
    }

    internal static string TranscribePcm(byte[] pcm)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0) return "";
        Vosk.Vosk.SetLogLevel(-1);
        using var model = new Model(ModelPath);
        using var recognizer = new VoskRecognizer(model, SampleRate);
        var segments = new List<string>();
        for (int offset = 0; offset < pcm.Length; offset += 4000)
        {
            int length = Math.Min(4000, pcm.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(pcm, offset, chunk, 0, length);
            if (recognizer.AcceptWaveform(chunk, length))
                AddResult(recognizer.Result(), segments);
        }
        AddResult(recognizer.FinalResult(), segments);
        return string.Concat(segments).Trim();
    }

    private static void AddResult(string json, List<string> segments)
    {
        using JsonDocument result = JsonDocument.Parse(json);
        if (result.RootElement.TryGetProperty("text", out JsonElement text) &&
            !string.IsNullOrWhiteSpace(text.GetString()))
            segments.Add(text.GetString()!);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _microphone.StopRecording();
        _microphone.Dispose();
        _pcm.Dispose();
    }
}
