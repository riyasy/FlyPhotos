using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Infra.Configuration;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsWrapper))]
[JsonSerializable(typeof(ObservableCollection<RawDecoder>))]
public partial class JsonSourceGenerationContext : JsonSerializerContext
{
}