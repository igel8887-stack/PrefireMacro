using System;
using System.Drawing;

namespace PrefireMacro
{
    // The caller owns target. A successful copy must be a distinct desktop presentation,
    // match the complete requested identity, and still be <=12ms old after copying.
    internal interface IRecentHudFrames
    {
        bool WaitingForNewFrames { get; }
        bool TryRead(IntPtr window, uint process, Rectangle client, Rectangle area,
                     Bitmap target, long notBefore, out long timestamp);
    }
}
