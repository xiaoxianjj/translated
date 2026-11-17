using System;

namespace Translated
{
    internal sealed class EditorSize
    {
        internal readonly double Width, Height, MinWidth, MinHeight;
        internal EditorSize(double width, double height, double minWidth, double minHeight)
        { Width=width; Height=height; MinWidth=minWidth; MinHeight=minHeight; }
    }
    internal static class EditorSizing
    {
        internal static EditorSize Fit(double workWidth, double workHeight, double preferredWidth, double preferredHeight)
        {
            // Small work areas must also lower the minimum: otherwise WPF raises the
            // requested size back above the available height and hides the footer.
            double width = Math.Max(1,workWidth-Math.Min(40,workWidth*.05));
            double height = Math.Max(1,workHeight-Math.Min(40,workHeight*.05));
            double minWidth=Math.Min(870,width), minHeight=Math.Min(660,height);
            return new EditorSize(Math.Max(minWidth,Math.Min(preferredWidth,width)),
                Math.Max(minHeight,Math.Min(preferredHeight,height)),minWidth,minHeight);
        }
    }
}
