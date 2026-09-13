using System.Text.Json.Serialization;

namespace OpenSuperWhisper.Core.Settings;

/// <summary>
/// Every user preference. Port of the mac app's <c>AppPreferences</c>.
/// </summary>
/// <remarks>
/// Property names match the mac app's <c>UserDefaults</c> keys exactly, and so do the
/// defaults â€” see docs/windows-port.md Â§6. Keeping them identical means a settings
/// file can be compared field by field across platforms when behaviour diverges.
/// <para>
/// Two mac keys are absent: <c>fluidAudioModelVersion</c> went with the Parakeet
/// engine, and <c>qwen3Variant</c> was unreferenced in the mac source.
/// </para>
/// </remarks>
public sealed class AppSettings
{
    // ---- Engine and model ----

    public string SelectedEngine { get; set; } = "whisper";

    /// <summary>Absolute path to the active model, or null to use the bundled default.</summary>
    public string? SelectedWhisperModelPath { get; set; }

    public string WhisperLanguage { get; set; } = "en";

    // ---- Transcription ----

    public bool SuppressBlankAudio { get; set; } = true;

    /// <summary>Emitting timestamps disables VAD trimming â€” it would invalidate them.</summary>
    public bool ShowTimestamps { get; set; }

    public double Temperature { get; set; }

    public double NoSpeechThreshold { get; set; } = 0.6;

    public string InitialPrompt { get; set; } = string.Empty;

    public bool UseBeamSearch { get; set; }

    public int BeamSize { get; set; } = 5;

    /// <summary>
    /// Reserved. The Rust autocorrect library is built but not yet called, so this
    /// value is carried and never read.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from the settings UI: a checkbox for a feature that does
    /// nothing is worse than no checkbox, because it makes the feature look broken
    /// rather than unbuilt. The key stays so a settings file survives the round trip
    /// and so the mac comparison in §6 of the plan still lines up.
    /// </remarks>
    public bool UseAsianAutocorrect { get; set; } = true;

    // ---- Trigger ----

    /// <summary>
    /// Bare modifier key used as the trigger.
    /// </summary>
    /// <remarks>
    /// Defaults to right Ctrl, not the mac's left Command. Right Alt would be the
    /// closer analogue of Option but is AltGr on many layouts, and the bound key is
    /// withheld from other applications â€” see the M5 notes in the plan.
    /// </remarks>
    public string ModifierOnlyHotkey { get; set; } = "RightControl";

    /// <summary>Last non-none modifier, so the choice survives switching modes.</summary>
    public string LastModifierOnlyHotkey { get; set; } = "RightControl";

    public string MouseButtonHotkey { get; set; } = "None";

    public bool HoldToRecord { get; set; } = true;

    public bool DoublePressToTrigger { get; set; }

    // ---- Insertion ----

    public bool AutoPasteTranscription { get; set; } = true;

    public bool AutoCopyToClipboard { get; set; }

    public bool AddSpaceAfterSentence { get; set; } = true;

    /// <summary>Type as Unicode instead of pasting. Windows-only; no mac counterpart.</summary>
    public bool UseUnicodeTyping { get; set; }

    // ---- Behaviour ----

    public bool PlaySoundOnRecordStart { get; set; }

    public bool EscCancelWithoutConfirmation { get; set; }

    public bool StartHiddenInTray { get; set; }

    public bool HasCompletedOnboarding { get; set; }

    public bool DebugMode { get; set; }

    // ---- Devices and retention ----

    /// <summary>WASAPI endpoint ID, which survives unplug and replug.</summary>
    public string? SelectedMicrophoneId { get; set; }

    // ---- History ----

    /// <summary>
    /// Whether transcripts and their audio are kept at all.
    /// </summary>
    /// <remarks>
    /// A dictation tool hears everything said near it, so this is worth being able to
    /// turn off outright. With it off nothing is written rather than written and
    /// hidden: no database row, and the captured audio is deleted after transcription.
    /// </remarks>
    public bool SaveTranscriptionHistory { get; set; } = true;

    public bool AutoDeleteRecordingsEnabled { get; set; }

    public int AutoDeleteRecordingsAfterDays { get; set; } = 30;

    /// <summary>Deep copy, so edits in a settings dialog can be discarded.</summary>
    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>
    /// Clamps the numeric decoding values into ranges whisper can actually use.
    /// </summary>
    /// <remarks>
    /// The settings file is meant to be edited by hand, so these values arrive
    /// unvalidated. A negative temperature or a beam size of zero does not fail
    /// loudly — it produces silently worse transcripts, which is the hardest kind of
    /// bug to attribute. Clamping rather than rejecting keeps one bad number from
    /// discarding every other preference in the file.
    /// <para>
    /// Retention days are deliberately not clamped: a non-positive value is the
    /// documented way to disable retention at the policy level, and rounding it up to
    /// one day would start deleting recordings nobody asked to delete.
    /// </para>
    /// </remarks>
    public AppSettings Normalize()
    {
        // Above 1.0 whisper's sampling is noise; below 0 it is meaningless.
        Temperature = Math.Clamp(Temperature, 0.0, 1.0);

        // A probability. At 0 every segment counts as speech, at 1 none does.
        NoSpeechThreshold = Math.Clamp(NoSpeechThreshold, 0.0, 1.0);

        // 1 is greedy decoding by another name; past 10 the cost grows and the
        // transcript does not improve.
        BeamSize = Math.Clamp(BeamSize, 1, 10);

        return this;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
