namespace LeXtudio.UI.Text.Core
{
    /// <summary>
    /// Compatibility enum for CoreText input scopes (WinUI parity).
    /// Only a small subset is implemented here; expand as needed.
    /// </summary>
    public enum CoreTextInputScope
    {
        Default = 0,
        Text = 1,
        Url = 2,
        EmailAddress = 3,
        Number = 4,
        TelephoneNumber = 5,
    }
}
