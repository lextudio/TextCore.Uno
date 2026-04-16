namespace LeXtudio.UI.Text.Core
{
    /// <summary>Represents a request for layout measurement with a given size.</summary>
    public class CoreTextLayoutRequest
    {
        /// <summary>The available width for layout (back-compat).</summary>
        public double Width { get; set; }

        /// <summary>The available height for layout (back-compat).</summary>
        public double Height { get; set; }

        /// <summary>
        /// Layout bounds carrying exact screen rectangles for text and control areas.
        /// This mirrors the WinUI layout request shape (TextBounds / ControlBounds).
        /// </summary>
        public CoreTextLayoutBounds LayoutBounds { get; set; } = new CoreTextLayoutBounds();
    }
}
