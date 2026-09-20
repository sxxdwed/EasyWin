using System.Windows;
using EasyWin.Desktop.Models;
using EasyWin.Desktop.Views;
using Microsoft.Win32;

namespace EasyWin.Desktop.Services;

public sealed class InteractionService : IInteractionService
{
    public string? SelectIso()
    {
        var dialog = new OpenFileDialog
        {
            Title = EasyWin.Core.Localization.DeploymentStrings.Get("Ui94"),
            Filter = EasyWin.Core.Localization.DeploymentStrings.Get("Ui95"),
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public bool ConfirmDisks(DiskChoice targetDisk, IReadOnlyList<DiskChoice> additionalDisksToErase)
    {
        DiskChoice[] disks = [targetDisk, .. additionalDisksToErase];
        string display = string.Join("\n", disks.Select((disk, index) =>
            index == 0 ? $"WINDOWS → {disk.DisplayName}" : $"ДАННЫЕ → {disk.DisplayName}"));
        string details = string.Join("\n", disks.Select(disk =>
            $"{disk.DeviceId}  |  Serial: {disk.Serial}  |  Bus: {disk.BusType}"));
        string requiredText = "ERASE " + string.Join(" + ", disks.Select(static disk => disk.DeviceId));
        var dialog = new ExactDiskConfirmationWindow(display, details, requiredText)
        {
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}
