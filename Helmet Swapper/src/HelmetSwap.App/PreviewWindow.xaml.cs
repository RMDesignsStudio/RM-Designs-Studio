using System.Windows;

namespace HelmetSwap.App;

/// <summary>The preview popped out of the main window: its own resizable window with the same 3D page.</summary>
public partial class PreviewWindow : Window
{
    internal PreviewHost Host { get; }

    public PreviewWindow()
    {
        InitializeComponent();
        Studio.Ui.StudioChrome.Apply(this, 44);   // borderless: the slim band is the title bar
        Host = new PreviewHost(View);
    }

    public string Status { set => StatusText.Text = value; }
}
