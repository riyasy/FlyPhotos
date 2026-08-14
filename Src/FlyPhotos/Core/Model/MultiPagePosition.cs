#nullable enable

namespace FlyPhotos.Core.Model;

/// <summary>
/// Which page of a multi-page photo (multi-page TIFF, multi-frame ICO) is on screen, and its pixel size.
/// <para><see cref="Path"/> tags the value with the photo it describes, so a value left over from the
/// previous photo can never be labelled against the new one.</para>
/// <para>Deliberately a class where <see cref="FileDisplayDetails"/> is a struct: this one is published
/// from the UI thread and read on PhotoDisplayController's STA thread. Only the reference assignment is
/// atomic — a five-field struct could be read torn (new Path with a stale Index).</para>
/// </summary>
internal sealed record MultiPagePosition(string Path, int Index, int Count, int Width, int Height);
