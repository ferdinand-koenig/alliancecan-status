using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AllianceStatus.Models;

namespace AllianceStatus.Services;

/// <summary>Loads and atomically saves <see cref="AppState"/> as JSON.</summary>
public sealed class StateStore
{
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllianceStatus");

    readonly string _path = Path.Combine(Folder, "state.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppState Load()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (!File.Exists(_path)) return AppState.CreateDefault();
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_path), Options) ?? AppState.CreateDefault();
            state.Normalize();
            return state;
        }
        catch (Exception ex)
        {
            Log.Error("State file unreadable, starting fresh", ex);
            try { File.Move(_path, $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            return AppState.CreateDefault();
        }
    }

    public void Save(AppState state)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save state", ex);
        }
    }
}
