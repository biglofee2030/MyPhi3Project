using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace LofeeVoiceMVP;

internal class AdvancedTTSHandler
{
    internal enum TTSEngine { GoogleTTS, AzureSpeech, EspeakNG, WindowsNative }
    
    private readonly TTSEngine _engine;
    private readonly string? _azureKey;
    private readonly string? _azureRegion;
    private volatile bool _isSpeaking;
    
    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    
    private const string GoogleTTSUrl = "https://translate.google.com/translate_tts";
    private const string GoogleUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
    private const int TextChunkSize = 200;
    private const int SpeechDelayMs = 300;

    static AdvancedTTSHandler()
    {
        _httpClient.DefaultRequestHeaders.Add("User-Agent", GoogleUA);
    }

    public bool IsSpeaking => _isSpeaking;

    public AdvancedTTSHandler() : this(TTSEngine.GoogleTTS) { }

    public AdvancedTTSHandler(TTSEngine engine = TTSEngine.GoogleTTS, string? azureKey = null, string? azureRegion = null)
    {
        _engine = engine;
        _azureKey = azureKey;
        _azureRegion = azureRegion;
    }

    public void StopSpeaking() => _isSpeaking = false;

    public async Task<string> SpeakAsync(string text, string language = "ar")
    {
        if (string.IsNullOrWhiteSpace(text))
            return "⚠️ النص فارغ";

        while (_isSpeaking)
            await Task.Delay(100);

        try
        {
            _isSpeaking = true;
            var result = _engine switch
            {
                TTSEngine.GoogleTTS => await SpeakWithGoogleTTSAsync(text, language),
                TTSEngine.AzureSpeech => await SpeakWithAzureSpeechAsync(text, language),
                TTSEngine.EspeakNG => await SpeakWithEspeakNGAsync(text, language),
                _ => await SpeakWithWindowsNativeAsync(text, language)
            };
            return result;
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
        finally
        {
            _isSpeaking = false;
        }
    }

    private async Task<string> SpeakWithGoogleTTSAsync(string text, string language)
    {
        try
        {
            var chunks = SplitText(text, TextChunkSize);
            foreach (var chunk in chunks)
            {
                if (!_isSpeaking)
                    break;

                var url = $"{GoogleTTSUrl}?ie=UTF-8&tl={language}&client=tw-ob&q={Uri.EscapeDataString(chunk)}";

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await _httpClient.GetAsync(url, cts.Token);
                
                if (!response.IsSuccessStatusCode)
                    continue;

                using var audioStream = await response.Content.ReadAsStreamAsync();
                await PlayAudioAsync(audioStream);
                await Task.Delay(SpeechDelayMs);
            }
            return "✅ تم التحدث";
        }
        catch (OperationCanceledException)
        {
            return "⚠️ انتهت مهلة الاتصال";
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    private async Task<string> SpeakWithAzureSpeechAsync(string text, string language)
    {
        if (string.IsNullOrEmpty(_azureKey) || string.IsNullOrEmpty(_azureRegion))
            return "❌ Azure API مش موجودة";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{_azureRegion}.tts.speech.microsoft.com/cognitiveservices/v1");
            request.Headers.Add("Ocp-Apim-Subscription-Key", _azureKey);
            request.Content = new StringContent(
                BuildSSML(text, language),
                Encoding.UTF8,
                "application/ssml+xml"
            );

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await _httpClient.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
                return $"❌ Azure: {response.StatusCode}";

            using var stream = await response.Content.ReadAsStreamAsync();
            await PlayAudioAsync(stream);
            return "✅ تم التحدث";
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    private async Task<string> SpeakWithEspeakNGAsync(string text, string language)
    {
        try
        {
            var espeakPath = @"C:\Program Files\eSpeak NG\espeak-ng.exe";
            if (!File.Exists(espeakPath))
                return "❌ eSpeak NG غير مثبت";

            var voiceCode = language == "ar" ? "ar" : "en";
            var audioFile = Path.GetTempFileName() + ".wav";

            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = espeakPath,
                Arguments = $"-v {voiceCode} -w \"{audioFile}\" \"{text}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();

                if (File.Exists(audioFile))
                {
                    using var stream = File.OpenRead(audioFile);
                    await PlayAudioAsync(stream);
                    File.Delete(audioFile);
                    return "✅ تم التحدث";
                }
            }
            return "❌ فشل التنفيذ";
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    private async Task<string> SpeakWithWindowsNativeAsync(string text, string language)
    {
        try
        {
            var assembly = Assembly.Load("System.Speech");
            var synthType = assembly.GetType("System.Speech.Synthesis.SpeechSynthesizer", false);
            if (synthType == null)
                return "❌ محرك Windows TTS غير متاح";

            var synth = Activator.CreateInstance(synthType);
            if (synth == null)
                return "❌ فشل في إنشاء SpeechSynthesizer";

            synth.GetType().GetMethod("SetOutputToDefaultAudioDevice")?.Invoke(synth, null);

            var getVoicesMethod = synthType.GetMethod("GetInstalledVoices");
            var voices = getVoicesMethod?.Invoke(synth, null) as System.Collections.IEnumerable;

            if (voices != null)
            {
                foreach (var voice in voices)
                {
                    var voiceInfo = voice.GetType().GetProperty("VoiceInfo")?.GetValue(voice);
                    var culture = voiceInfo?.GetType().GetProperty("Culture")?.GetValue(voiceInfo) as System.Globalization.CultureInfo;
                    
                    if (culture?.Name.StartsWith(language, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var name = voiceInfo?.GetType().GetProperty("Name")?.GetValue(voiceInfo)?.ToString();
                        synthType.GetMethod("SelectVoice", new[] { typeof(string) })?.Invoke(synth, new object[] { name! });
                        break;
                    }
                }
            }

            synthType.GetMethod("Speak", new[] { typeof(string) })?.Invoke(synth, new object[] { text });

            (synth as IDisposable)?.Dispose();
            return "✅ تم التحدث";
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    private async Task PlayAudioAsync(Stream audioStream)
    {
        try
        {
            using var waveReader = new WaveFileReader(audioStream);
            using var player = new WaveOutEvent();
            player.Init(waveReader);
            player.Play();

            while (player.PlaybackState == PlaybackState.Playing && _isSpeaking)
                await Task.Delay(100);
        }
        catch
        {
            try
            {
                if (audioStream.CanSeek)
                    audioStream.Position = 0;

                using var mediaReader = new StreamMediaFoundationReader(audioStream);
                using var player = new WaveOutEvent();
                player.Init(mediaReader);
                player.Play();

                while (player.PlaybackState == PlaybackState.Playing && _isSpeaking)
                    await Task.Delay(100);
            }
            catch { }
        }
    }

    private static List<string> SplitText(string text, int maxLength)
    {
        var chunks = new List<string>();
        var words = text.Split(' ');
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if ((current.Length + word.Length + 1) > maxLength)
            {
                if (current.Length > 0)
                    chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0)
                current.Append(" ");
            current.Append(word);
        }

        if (current.Length > 0)
            chunks.Add(current.ToString());
        return chunks;
    }

    private static string BuildSSML(string text, string language)
    {
        var voice = language == "ar" ? "ar-SA-GhadaNeural" : "en-US-AriaNeural";
        return $@"<speak version='1.0' xml:lang='{language}'>
<voice name='{voice}'>
<prosody rate='0.9'>{EscapeXml(text)}</prosody>
</voice>
</speak>";
    }

    private static string EscapeXml(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
