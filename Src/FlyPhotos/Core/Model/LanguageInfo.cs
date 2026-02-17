#nullable enable
namespace FlyPhotos.Core.Model;

public class LanguageInfo(string displayName, string nativeName, string languageCode)
{
    public string DisplayName { get; } = displayName;
    public string NativeName { get; } = nativeName;
    public string LanguageCode { get; } = languageCode;
}