#nullable enable
namespace FlyPhotos.Core.Model;

internal class RenameResult(bool success, string failMessage = "")
{
    public bool Success { get; } = success;
    public string FailMessage { get; } = failMessage;
}
