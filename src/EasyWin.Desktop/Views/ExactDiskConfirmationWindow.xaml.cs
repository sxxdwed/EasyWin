using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace EasyWin.Desktop.Views;

public partial class ExactDiskConfirmationWindow : Window, INotifyPropertyChanged
{
    private string _enteredText = string.Empty;

    public ExactDiskConfirmationWindow(string diskDisplayName, string diskDetails, string requiredText)
    {
        DiskDisplayName = diskDisplayName;
        DiskDetails = diskDetails;
        RequiredText = requiredText;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => ConfirmationTextBox.Focus();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DiskDisplayName { get; }
    public string DiskDetails { get; }
    public string RequiredText { get; }

    public string EnteredText
    {
        get => _enteredText;
        set
        {
            if (string.Equals(_enteredText, value, StringComparison.Ordinal))
            {
                return;
            }

            _enteredText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnteredText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMatch)));
        }
    }

    public bool IsMatch => string.Equals(EnteredText.Trim(), RequiredText, StringComparison.Ordinal);

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (IsMatch)
        {
            DialogResult = true;
        }
    }
}
