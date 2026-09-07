using System.Text.Json;
using OpenSuperWhisper.Core.Diagnostics;

namespace OpenSuperWhisper.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON.
/// </summary>
/// <remarks>
/// Replaces the mac app's <c>UserDefaults</c>. A plain JSON file rather than the
/// registry: it can be read, diffed, backed up and deleted by hand, which matters for
/// a tool whose misbehaviour is usually a wrong preference.
/// </remarks>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;

        var existed = File.Exists(_path);
        _current = Load();

        // Write defaults on first run. The file is meant to be readable and editable by
        // hand, which it cannot be while it does not exist — and "where are the
        // settings?" is otherwise unanswerable until something happens to change one.
        if (!existed) Save(_current);
    }

    public string Path => _path;

    /// <summary>The live settings. Treat as read-only; use <see cref="Update"/> to change.</summary>
    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Raised after settings change, so listeners can re-apply them.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>Applies an edit and persists it.</summary>
    public void Update(Action<AppSettings> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        AppSettings updated;

        lock (_gate)
        {
            // Edit a copy and swap: a reader holding Current never sees a half-applied
            // change, and a throwing edit leaves the previous settings intact.
            updated = _current.Clone();
            edit(updated);
            _current = updated;
        }

        Save(updated);
        Changed?.Invoke(this, updated);
    }

    /// <summary>Replaces everything, e.g. when a settings dialog is accepted.</summary>
    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate) _current = settings.Clone();

        Save(settings);
        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);

            // A file that parses to null is corrupt in a way that would otherwise
            // surface as a NullReferenceException much later.
            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Never let a damaged settings file stop the app starting. Defaults are
            // always a working configuration, which is not true of half-read settings.
            Log.Error($"could not read settings from {_path}, using defaults", ex);
            return new AppSettings();
        }
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

            // Write to a temporary file and move into place. A crash mid-write would
            // otherwise leave a truncated file, and the next launch would silently
            // reset every preference.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"could not save settings to {_path}", ex);
        }
    }
}
