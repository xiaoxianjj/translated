namespace Translated
{
    internal enum SelectionDelivery { Review, Copy, Replace }
    internal static class SelectionPolicy
    {
        internal static bool KeyboardGesture(int key, bool shift, bool ctrl, bool alt, bool windows)
        {
            if (alt || windows) return false;
            if (key == 0x41) return ctrl && !shift;
            return shift && (key >= 0x21 && key <= 0x28);
        }
        internal static SelectionDelivery Delivery(bool writable, bool replace, bool monitoring, bool stable, bool clipboardUnchanged)
        {
            if (!stable || !clipboardUnchanged) return SelectionDelivery.Review;
            if (!writable) return SelectionDelivery.Copy;
            return replace && monitoring ? SelectionDelivery.Replace : SelectionDelivery.Review;
        }
    }
}
