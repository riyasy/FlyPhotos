using System.Text.Json.Serialization;

namespace FlyPhotos.AppSettings;

[JsonSourceGenerationOptions(WriteIndented = true)] // Keep other options you need
[JsonSerializable(typeof(SettingsWrapper))]
// By including SettingsWrapper, the generator also automatically includes AppSettings.
public partial class JsonSourceGenerationContext : JsonSerializerContext
{
}