namespace LeXtudio.UI.Text.Core
{
    /// <summary>Represents a selection request or response (start and length in document coordinates).</summary>
    public class CoreTextSelectionRequest
    {
        /// <summary>The zero-based start offset of the selection.</summary>
        public int Start { get; set; }

        /// <summary>The length of the selection in characters.</summary>
        public int Length { get; set; }

        /// <summary>Compatibility: start position named like WinUI `StartCaretPosition`.</summary>
        public int StartCaretPosition
        {
            get => Start;
            set => Start = value;
        }

        /// <summary>Compatibility: end position named like WinUI `EndCaretPosition`.</summary>
        public int EndCaretPosition
        {
            get => Start + Length;
            set => Length = System.Math.Max(0, value - Start);
        }
    }
}
