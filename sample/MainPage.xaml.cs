using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeXtudio.UI.Text.Core.Sample;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        this.SizeChanged += OnPageSizeChanged;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImeTextBox.Width = e.NewSize.Width;
        ImeTextBox.Height = e.NewSize.Height;
    }
}
