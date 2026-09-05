namespace OpenSuperWhisper.Core.Transcription;

/// <summary>
/// Decoding settings. Names and defaults mirror the mac app's preference keys so the
/// two builds can be compared field by field — see docs/windows-port.md §6.
/// </summary>
public sealed class TranscriptionSettings
{
    /// <summary>BCP-47-ish whisper language code, or "auto" to let whisper detect.</summary>
    public string Language { get; set; } = "en";

    public bool SuppressBlankAudio { get; set; } = true;

    /// <summary>
    /// When true, timestamps are emitted and VAD trimming is skipped — trimmed audio
    /// would make every timestamp wrong relative to the original file.
    /// </summary>
    public bool ShowTimestamps { get; set; }

    public float Temperature { get; set; }

    public float NoSpeechThreshold { get; set; } = 0.6f;

    public string InitialPrompt { get; set; } = string.Empty;

    public bool UseBeamSearch { get; set; }

    public int BeamSize { get; set; } = 5;
}
