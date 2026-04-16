using System;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Represents a mutable text request used by <see cref="CoreTextEditContext"/> handlers.
    /// </summary>
    public class CoreTextTextRequest
    {
        /// <summary>Initializes a new instance with the provided text.</summary>
        public CoreTextTextRequest(string text) => Text = text;

        /// <summary>The text payload for the request. Handlers may set this value.</summary>
        public string Text { get; set; }

        /// <summary>
        /// Compatibility alias: WinUI uses a `Range` with `StartCaretPosition`/`EndCaretPosition`.
        /// Handlers may set this to indicate the requested range.
        /// </summary>
        public CoreTextRange Range { get; set; } = new CoreTextRange();

        /// <summary>Gets a deferral that can be used to signal asynchronous completion.</summary>
        public CoreTextDeferral GetDeferral() => new CoreTextDeferral();
    }
}
