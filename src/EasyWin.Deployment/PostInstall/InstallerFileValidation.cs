using System.Reflection.PortableExecutable;
using EasyWin.Core.Localization;

namespace EasyWin.Deployment.PostInstall;

public static class InstallerFileValidation
{
    public static void ValidateExecutable(string path, long maxBytes)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 256 || stream.Length > maxBytes) throw new BadImageFormatException();
            using var pe = new PEReader(stream);
            var header = pe.PEHeaders;
            // An x86 bootstrap can carry x64 payloads. ARM/IA64 cannot run on our x64 target.
            if (header.PEHeader is null || header.CoffHeader.Machine is not (Machine.I386 or Machine.Amd64) ||
                !header.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage) ||
                header.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) || header.SectionHeaders.Length == 0)
                throw new BadImageFormatException();
            foreach (var section in header.SectionHeaders)
                if (section.PointerToRawData < 0 || section.SizeOfRawData < 0 ||
                    (long)section.PointerToRawData + section.SizeOfRawData > stream.Length) throw new BadImageFormatException();
        }
        catch (BadImageFormatException error)
        { throw new InvalidDataException(DeploymentStrings.Get("PackageDownloadInvalid"), error); }
    }
}
