using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LofeeVoiceMVP;

internal class AudioVoiceService : IDisposable
{
    private readonly AdvancedAudioEngine _audioEngine;
    private readonly AdvancedTTSHandler _ttsHandler;
    private readonly AudioSessionManager _sessionManager;
    private volatile bool _isInitialized = false;
    private volatile bool _isProcessing = false;
    private bool _disposed = false;

    public bool IsInitialized => _isInitialized;
    public bool IsSpeaking => _ttsHandler?.IsSpeaking ?? false;
    public bool IsProcessing => _isProcessing;

    public event EventHandler<AudioErrorEventArgs>? ErrorOccurred;
    public event EventHandler<VoiceSessionEventArgs>? SessionStarted;
    public event EventHandler<VoiceSessionEventArgs>? SessionEnded;
    public event EventHandler<AudioQualityEventArgs>? AudioQualityChanged;

    public AudioVoiceService(AdvancedTTSHandler? ttsHandler = null)
    {
        _ttsHandler = ttsHandler ?? new AdvancedTTSHandler();
        _audioEngine = new AdvancedAudioEngine();
        _sessionManager = new AudioSessionManager();
    }

    public async Task<bool> InitializeAsync()
    {
        if (_isInitialized)
            return true;

        try
        {
            Console.WriteLine("\n" + new string('═', 60));
            Console.WriteLine("🎙️  تهيئة خدمة الصوت...");
            Console.WriteLine(new string('═', 60));

            Console.WriteLine("\n📡 الخطوة 1: محرك الصوت...");
            if (!await _audioEngine.InitializeAdvancedAudioAsync())
            {
                RaiseError("فشل تهيئة محرك الصوت");
                return false;
            }

            Console.WriteLine("\n🎧 الخطوة 2: Spatial Audio...");
            var dolbyResult = await _audioEngine.EnableDolbySpatialAudioAsync();
            Console.WriteLine(dolbyResult);

            Console.WriteLine("\n💾 الخطوة 3: مدير الجلسات...");
            await _sessionManager.InitializeAsync();
            Console.WriteLine("✅ جاهز");

            Console.WriteLine("\n🔊 الخطوة 4: اختبار الجودة...");
            var testResult = await TestAudioQualityAsync();
            Console.WriteLine(testResult);

            _isInitialized = true;
            Console.WriteLine("\n" + new string('═', 60));
            Console.WriteLine("✅ الخدمة جاهزة!");
            Console.WriteLine(new string('═', 60) + "\n");

            SessionStarted?.Invoke(this, new VoiceSessionEventArgs());
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"خطأ: {ex.Message}");
            return false;
        }
    }

    public async Task<AudioProcessingResult> ProcessAudioAsync(
        byte[] audioData,
        AudioProcessingOptions? options = null
    )
    {
        ThrowIfDisposed();
        if (!_isInitialized)
            return AudioProcessingResult.Error("الخدمة مش مهيأة");
        if (_isProcessing)
            return AudioProcessingResult.Error("معالجة قيد التنفيذ");

        try
        {
            _isProcessing = true;
            options ??= AudioProcessingOptions.Default;

            var analysis = await _audioEngine.AnalyzeAudioAsync(audioData);
            Console.WriteLine($"📊 جودة {analysis.Quality}, وضوح {analysis.Clarity}%");

            var processingMsg = await _audioEngine.ProcessAudioWithEffectsAsync(
                audioData,
                options.ApplyEnhancement,
                options.ApplySpatialAudio
            );
            Console.WriteLine(processingMsg);

            if (options.EnhanceVoice)
            {
                var enhancedMsg = await _audioEngine.EnhanceVoiceAsync(audioData);
                Console.WriteLine(enhancedMsg);
            }

            await _sessionManager.LogAudioProcessingAsync(analysis, options);

            AudioQualityChanged?.Invoke(this, new AudioQualityEventArgs 
            { 
                QualityScore = analysis.Clarity,
                Rating = GetQualityRating(analysis)
            });

            return new AudioProcessingResult
            {
                Success = true,
                Analysis = analysis,
                Message = "✅ معالجة نجحت"
            };
        }
        catch (Exception ex)
        {
            return AudioProcessingResult.Error($"خطأ: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    public async Task<TTSResult> SpeakAsync(string text, TTSOptions? options = null)
    {
        ThrowIfDisposed();
        if (!_isInitialized)
            return TTSResult.Error("الخدمة مش مهيأة");
        if (string.IsNullOrWhiteSpace(text))
            return TTSResult.Error("النص فارغ");

        try
        {
            options ??= TTSOptions.Default;

            if (!options.AllowConcurrent)
            {
                while (_ttsHandler.IsSpeaking)
                    await Task.Delay(100);
            }

            Console.WriteLine($"🗣️  {text.Substring(0, Math.Min(50, text.Length))}...");

            var result = await _ttsHandler.SpeakAsync(text, options.Language);
            await _sessionManager.LogTTSAsync(text, options.Language, result);

            return new TTSResult
            {
                Success = true,
                Message = result,
                Language = options.Language
            };
        }
        catch (Exception ex)
        {
            return TTSResult.Error($"خطأ: {ex.Message}");
        }
    }

    public void StopSpeaking()
    {
        _ttsHandler?.StopSpeaking();
        Console.WriteLine("⏹️ إيقاف");
    }

    public async Task<string> SetVolumeAsync(float volume)
    {
        ThrowIfDisposed();
        if (!_isInitialized)
            return "❌ خدمة مش مهيأة";
        if (volume < 0 || volume > 200)
            return "❌ 0-200% فقط";

        var result = await _audioEngine.SetVolumeAsync(volume / 100f);
        await _sessionManager.LogVolumeChangeAsync(volume);
        return result;
    }

    public async Task<string> SetEqualizerAsync(int band, float gainDb)
    {
        ThrowIfDisposed();
        if (!_isInitialized)
            return "❌ خدمة مش مهيأة";
        if (band < 0 || band > 4)
            return "❌ 0-4 فقط";
        if (gainDb < -12 || gainDb > 12)
            return "❌ -12 إلى +12 dB";

        var result = await _audioEngine.SetEqualizerBandAsync(band, gainDb);
        await _sessionManager.LogEqualizerChangeAsync(band, gainDb);
        return result;
    }

    public async Task<string> TestAudioQualityAsync()
    {
        try
        {
            var testData = GenerateTestAudioData();
            var analysis = await _audioEngine.AnalyzeAudioAsync(testData);

            return $@"📊 جودة الصوت:
━━━━━━━━━━━━━━━━━━━━━━━━━━
  معدل: {analysis.SampleRate} Hz
  قنوات: {analysis.ChannelCount}
  عمق: {analysis.BitsPerSample} bit
  مدة: {analysis.Duration.TotalSeconds:F2} s
  
  تردد: {analysis.Frequency}
  جودة: {analysis.Quality}
  ضوضاء: {analysis.NoiseLevel}%
  وضوح: {analysis.Clarity}%
  
✅ النتيجة: {GetQualityRating(analysis)}";
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    public async Task<string> GetSessionReportAsync()
    {
        try
        {
            return await _sessionManager.GenerateReportAsync();
        }
        catch (Exception ex)
        {
            return $"❌ خطأ: {ex.Message}";
        }
    }

    public async Task<bool> ClearSessionHistoryAsync()
    {
        try
        {
            await _sessionManager.ClearHistoryAsync();
            Console.WriteLine("✅ مسح السجل");
            return true;
        }
        catch (Exception ex)
        {
            RaiseError($"خطأ: {ex.Message}");
            return false;
        }
    }

    private byte[] GenerateTestAudioData()
    {
        var length = 48000 * 2;
        var data = new byte[length * 4];
        for (int i = 0; i < length; i++)
        {
            var value = (int)(32767 * Math.Sin(2 * Math.PI * 440 * i / 48000));
            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, data, i * 4, 4);
        }
        return data;
    }

    private string GetQualityRating(AdvancedAudioEngine.AudioAnalysis analysis)
    {
        var score = 0;
        if (analysis.Quality == "عالية جداً") score += 40;
        else if (analysis.Quality == "متوسطة") score += 20;

        score += analysis.Clarity / 5;
        score -= analysis.NoiseLevel / 2;

        return score >= 80 ? "⭐⭐⭐⭐⭐ ممتاز"
             : score >= 60 ? "⭐⭐⭐⭐ جيد"
             : score >= 40 ? "⭐⭐⭐ متوسط"
             : "⭐⭐ محتاج تحسين";
    }

    private void RaiseError(string message)
    {
        Console.WriteLine($"❌ {message}");
        ErrorOccurred?.Invoke(this, new AudioErrorEventArgs { Message = message });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioVoiceService));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _audioEngine?.Cleanup();
        _ttsHandler?.StopSpeaking();
        _sessionManager?.Dispose();

        SessionEnded?.Invoke(this, new VoiceSessionEventArgs());
    }
}

public class AudioProcessingOptions
{
    public bool ApplyEnhancement { get; set; } = true;
    public bool ApplySpatialAudio { get; set; } = true;
    public bool EnhanceVoice { get; set; } = true;
    public float VolumeLevel { get; set; } = 1.0f;
    public static AudioProcessingOptions Default => new();
    public static AudioProcessingOptions HighQuality => new() { ApplyEnhancement = true, ApplySpatialAudio = true, EnhanceVoice = true };
}

public class AudioProcessingResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public dynamic? Analysis { get; set; }
    public static AudioProcessingResult Error(string message) => new() { Success = false, Message = message };
}

public class TTSOptions
{
    public string Language { get; set; } = "ar";
    public bool AllowConcurrent { get; set; } = false;
    public float Speed { get; set; } = 1.0f;
    public float Pitch { get; set; } = 1.0f;
    public static TTSOptions Default => new();
    public static TTSOptions Arabic => new() { Language = "ar" };
    public static TTSOptions English => new() { Language = "en" };
}

public class TTSResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string Language { get; set; } = "";
    public static TTSResult Error(string message) => new() { Success = false, Message = message };
}

public class AudioErrorEventArgs : EventArgs
{
    public string Message { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class VoiceSessionEventArgs : EventArgs
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString();
    public DateTime StartTime { get; set; } = DateTime.Now;
    public string Details { get; set; } = "";
}

public class AudioQualityEventArgs : EventArgs
{
    public int QualityScore { get; set; }
    public string Rating { get; set; } = "";
}

internal class AudioSessionManager : IDisposable
{
    private List<AudioSessionLog> _sessions = new();
    private AudioSessionLog? _currentSession;

    public async Task InitializeAsync()
    {
        _currentSession = new AudioSessionLog
        {
            Id = Guid.NewGuid().ToString(),
            StartTime = DateTime.Now,
            Events = new List<string>()
        };
    }

    public async Task LogAudioProcessingAsync(AdvancedAudioEngine.AudioAnalysis analysis, AudioProcessingOptions options)
    {
        _currentSession?.Events.Add($"[{DateTime.Now:HH:mm:ss}] صوت: {analysis.Quality}, وضوح {analysis.Clarity}%");
        if (_currentSession != null) _currentSession.AudioProcessingCount++;
    }

    public async Task LogTTSAsync(string text, string language, string result)
    {
        _currentSession?.Events.Add($"[{DateTime.Now:HH:mm:ss}] TTS ({language}): {text.Substring(0, Math.Min(30, text.Length))}...");
        if (_currentSession != null) _currentSession.TTSCount++;
    }

    public async Task LogVolumeChangeAsync(float volume)
    {
        _currentSession?.Events.Add($"[{DateTime.Now:HH:mm:ss}] صوت: {(int)volume}%");
    }

    public async Task LogEqualizerChangeAsync(int band, float gainDb)
    {
        _currentSession?.Events.Add($"[{DateTime.Now:HH:mm:ss}] معادل - نطاق {band}: {gainDb:+0.0;-0.0} dB");
    }

    public async Task<string> GenerateReportAsync()
    {
        if (_currentSession == null)
            return "❌ لا توجد جلسة";

        var duration = DateTime.Now - _currentSession.StartTime;
        return $@"📋 التقرير:
━━━━━━━━━━━━━━━━━━━━━━━━━━
  معرّف: {_currentSession.Id}
  مدة: {duration.TotalSeconds:F0} s
  معالجة صوت: {_currentSession.AudioProcessingCount}
  TTS: {_currentSession.TTSCount}
  أحداث: {_currentSession.Events.Count}

🔍 الأحداث:
{string.Join("\n", _currentSession.Events.Take(10))}
{(_currentSession.Events.Count > 10 ? $"\n... و {_currentSession.Events.Count - 10} أخرى" : "")}";
    }

    public async Task ClearHistoryAsync()
    {
        _sessions.Clear();
        if (_currentSession != null)
        {
            _sessions.Add(_currentSession);
            _currentSession = new AudioSessionLog
            {
                Id = Guid.NewGuid().ToString(),
                StartTime = DateTime.Now,
                Events = new List<string>()
            };
        }
    }

    public void Dispose()
    {
        _currentSession = null;
        _sessions.Clear();
    }

    private class AudioSessionLog
    {
        public string Id { get; set; } = "";
        public DateTime StartTime { get; set; }
        public List<string> Events { get; set; } = new();
        public int AudioProcessingCount { get; set; }
        public int TTSCount { get; set; }
    }
}
