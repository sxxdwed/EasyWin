using EasyWin.Desktop.Models;

namespace EasyWin.Desktop.Services;

public interface IInteractionService
{
    string? SelectIso();
    bool ConfirmDisks(DiskChoice targetDisk, IReadOnlyList<DiskChoice> additionalDisksToErase);
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
}
