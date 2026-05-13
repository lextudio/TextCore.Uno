namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Represents a zero-based caret range with explicit start/end positions
    /// matching the WinUI `CoreTextRange` naming used by `TextView.PlatformInput`.
    /// </summary>
    public sealed class CoreTextRange
    {
        public int StartCaretPosition { get; set; }
        public int EndCaretPosition { get; set; }
    }
}
