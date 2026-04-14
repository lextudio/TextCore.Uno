using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeXtudio.UI.Text.Core.Sample;

public partial class App : Application
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Console.WriteLine("CoreText Sample: OnLaunched");

        var window = new Window();

        if (window.Content is not Frame rootFrame)
        {
            rootFrame = new Frame();
            window.Content = rootFrame;
        }

        rootFrame.Navigate(typeof(MainPage));
        Console.WriteLine("CoreText Sample: Navigated to MainPage");
        // Set a small explicit frame size so the native window may size to content on some platforms.
        try
        {
            // Request a compact frame size so the native window can be small
            rootFrame.Width = 320;
            // Use a smaller height request so the Window content fits a single-line control.
            rootFrame.Height = 56;
        }
        catch { }
        window.Activate();
        Console.WriteLine("CoreText Sample: Window activated");
    }
}
