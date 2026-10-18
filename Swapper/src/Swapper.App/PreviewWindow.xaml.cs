using System.Windows;

namespace Swapper.App;

/// <summary>The preview popped out of the main window: its own resizable window with the same 3D page.</summary>
public partial class PreviewWindow : Window
{
    internal ViewerHost Host { get; }

    public PreviewWindow()
    {
        InitializeComponent();
        Studio.Ui.StudioChrome.Apply(this, 44);   // borderless: the slim band is the title bar
        Host = new ViewerHost(View);
    }

    public string Status { set => StatusText.Text = value; }
}
