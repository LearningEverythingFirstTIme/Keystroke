using System.Windows;

namespace KeystrokeApp.Views;

public partial class ConsentDialog : Window
{
    /// <summary>
    /// True if the user accepted the consent terms.
    /// </summary>
    public bool Accepted { get; private set; }

    public ConsentDialog()
    {
        InitializeComponent();
    }

    private void AgreeCheck_Changed(object sender, RoutedEventArgs e)
    {
        AcceptBtn.IsEnabled = AgreeCheck.IsChecked == true;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
