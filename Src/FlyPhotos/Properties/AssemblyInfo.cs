using System.Runtime.CompilerServices;

// The tests drive the display pipeline (controller, prefetch cache, planner, photo list) through its internal
// types and test seams, without making them public.
[assembly: InternalsVisibleTo("FlyPhotos.Tests")]
