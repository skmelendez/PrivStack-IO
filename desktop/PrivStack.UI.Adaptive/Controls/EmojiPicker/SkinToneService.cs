using System.Text.Json;

namespace PrivStack.UI.Adaptive.Controls.EmojiPicker;

/// <summary>
/// Persists and manages the user's preferred skin tone for emoji rendering.
/// </summary>
public static class SkinToneService
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PrivStack", "skin-tone-preference.json");

    private static SkinTone _preferredTone = SkinTone.None;
    private static bool _loaded;

    /// <summary>Current preferred skin tone.</summary>
    public static SkinTone PreferredTone
    {
        get
        {
            EnsureLoaded();
            return _preferredTone;
        }
        set
        {
            _preferredTone = value;
            Save();
        }
    }

    /// <summary>
    /// Applies the preferred skin tone to an emoji if it supports it.
    /// </summary>
    public static string ApplyPreferred(string baseEmoji, bool supportsSkinTone)
    {
        if (!supportsSkinTone || _preferredTone == SkinTone.None)
            return baseEmoji;

        return SkinToneModifier.Apply(baseEmoji, _preferredTone);
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        Load();
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(PreferencePath)) return;

            var json = File.ReadAllText(PreferencePath);
            var data = JsonSerializer.Deserialize<SkinTonePreference>(json);
            if (data is not null && Enum.IsDefined(data.Tone))
                _preferredTone = data.Tone;
        }
        catch
        {
            _preferredTone = SkinTone.None;
        }
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(PreferencePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(new SkinTonePreference { Tone = _preferredTone });
            File.WriteAllText(PreferencePath, json);
        }
        catch
        {
            // Non-critical — ignore persistence failures
        }
    }

    private sealed class SkinTonePreference
    {
        public SkinTone Tone { get; set; }
    }
}
