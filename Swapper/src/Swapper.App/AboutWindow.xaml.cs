using System.Windows;

namespace Swapper.App;

/// <summary>Credits and the licence notice.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Studio.Ui.StudioChrome.Apply(this, 44);   // borderless: the slim band is the title bar
        VersionText.Text = "v" + MainWindow.AppVersion;
    }

    void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { }
        e.Handled = true;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
