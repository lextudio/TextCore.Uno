namespace LeXtudio.UI.Text.Core
{
    /// <summary>Simple rect structure used by CoreText layout bounds.</summary>
    public sealed class CoreTextRect
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>Layout bounds container with TextBounds and ControlBounds (screen coords).</summary>
    public sealed class CoreTextLayoutBounds
    {
        public CoreTextRect TextBounds { get; set; } = new CoreTextRect();
        public CoreTextRect ControlBounds { get; set; } = new CoreTextRect();
    }
}
