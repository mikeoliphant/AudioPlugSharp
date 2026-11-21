using System.Text.Json;
using AudioIOSharp;
using AudioPlugSharp;

namespace AudioPlugSharpStandalone;

/// <summary>
/// The standalone host's audio settings, kept next to the plugin's state (<c>AudioSettings.json</c> in the save
/// folder): the configuration in use, and the last one used with each driver, so choosing a driver again brings back
/// its device, channels, rate and buffer size.
/// </summary>
internal class AudioSettings
{
    const string FileName = "AudioSettings.json";
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AudioConfig Config { get; set; } = new();

    public Dictionary<string, AudioConfig> ByDriver { get; set; } = new();

    /// <summary>The configuration last used with a driver, or null.</summary>
    public AudioConfig? For(string driver) =>
        ByDriver.FirstOrDefault(d => string.Equals(d.Key, driver, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A configuration is in use: it's the one to start with next time, and the one to come back to for its
    /// driver.</summary>
    public void Remember(AudioConfig config)
    {
        Config = config;
        foreach (string key in ByDriver.Keys.Where(k => string.Equals(k, config.Driver, StringComparison.OrdinalIgnoreCase)).ToList())
            ByDriver.Remove(key);
        ByDriver[config.Driver] = config;
    }

    public static AudioSettings Load(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return new AudioSettings();
        try
        {
            return JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(path), Options) ?? new AudioSettings();
        }
        catch (Exception e)
        {
            Logger.Log("Could not read the audio settings, so starting from the defaults: " + e.Message);
            return new AudioSettings();
        }
    }

    public void Save(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));
        }
        catch (Exception e)
        {
            Logger.Log("Could not save the audio settings: " + e.Message);
        }
    }
}
