using System;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>Event arguments used when updating the current selection.</summary>
    public class CoreTextSelectionUpdatingEventArgs : EventArgs
    {
        /// <summary>The proposed new start offset for the selection.</summary>
        public int NewStart { get; set; }

        /// <summary>The proposed new length for the selection.</summary>
        public int NewLength { get; set; }

        /// <summary>
        /// Compatibility alias: WinUI exposes a `Selection` that is a `CoreTextRange`.
        /// Map this to the existing `NewStart`/`NewLength` fields for source compatibility.
        /// </summary>
        public CoreTextRange Selection
        {
            get => new CoreTextRange { StartCaretPosition = NewStart, EndCaretPosition = NewStart + NewLength };
            set
            {
                if (value is null)
                {
                    NewStart = 0;
                    NewLength = 0;
                }
                else
                {
                    NewStart = value.StartCaretPosition;
                    NewLength = System.Math.Max(0, value.EndCaretPosition - value.StartCaretPosition);
                }
            }
        }
    }
}
