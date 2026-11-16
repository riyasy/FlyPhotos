using System.Threading.Tasks;
using FlyPhotos.Core.Model;

namespace FlyPhotos.Display.ImageRendering;

/// <summary>A renderer that shows one page of several (multi-page TIFF, ICO frames, PDF) and can switch pages.</summary>
internal interface IMultiPage
{
    /// <summary>How many pages there are.</summary>
    int PageCount { get; }

    /// <summary>The page on screen.</summary>
    int CurrentPageIndex { get; }

    /// <summary>
    /// Shows a page, returning its size so the caller can re-fit the view when pages differ in size. Null when
    /// there is nothing to fit: the load failed, or a newer load superseded it. The page must be in range.
    /// </summary>
    Task<Size> LoadPageAsync(int pageIndex);
}
