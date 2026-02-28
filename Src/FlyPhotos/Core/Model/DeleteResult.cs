namespace FlyPhotos.Core.Model;

internal class DeleteResult(bool deleteSuccess, bool isLastPhoto, string failMessage = "")
{
    public bool DeleteSuccess { get; } = deleteSuccess;
    public bool IsLastPhoto { get; } = isLastPhoto;
    public string FailMessage { get; } = failMessage;
}
