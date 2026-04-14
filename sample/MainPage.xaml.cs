using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeXtudio.UI.Text.Core.Sample;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        this.Loaded += MainPage_Loaded;
    }

    private void MainPage_Loaded(object? sender, RoutedEventArgs e)
    {
        Console.WriteLine("CoreText Sample: MainPage loaded");
    }
}
