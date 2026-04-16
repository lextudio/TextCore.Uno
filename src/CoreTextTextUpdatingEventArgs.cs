using System;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>Event arguments for a text-updating operation.</summary>
    public class CoreTextTextUpdatingEventArgs : EventArgs
    {
        /// <summary>Creates a new instance with the proposed new text.</summary>
        public CoreTextTextUpdatingEventArgs(string newText)
        {
            NewText = newText;
            Range = new CoreTextRange();
            NewSelection = new CoreTextRange();
        }

        /// <summary>The new text being applied; handlers may modify this value.</summary>
        public string NewText { get; set; }

        /// <summary>Compatibility alias: WinUI uses a `Range` with StartCaretPosition/EndCaretPosition.</summary>
        public CoreTextRange Range { get; set; }

        /// <summary>Compatibility alias: proposed new selection after the update.</summary>
        public CoreTextRange NewSelection { get; set; }

        /// <summary>Windows-compatible property name for the text payload.</summary>
        public string Text { get => NewText; set => NewText = value; }
    }
}
