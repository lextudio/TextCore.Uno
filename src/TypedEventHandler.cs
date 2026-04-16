using System;

namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Simple replacement for WinRT's TypedEventHandler&lt;TSender, TResult&gt;.
    /// </summary>
    public delegate void TypedEventHandler<TSender, TResult>(TSender sender, TResult args);
}
