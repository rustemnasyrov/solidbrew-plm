using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: ArchiveCheck ZIP SLDASM SLDDRW");
    return 2;
}
await using var source = File.OpenRead(args[0]);
if (source.Length > CadZip.MaxArchiveBytes) throw new InvalidDataException("Archive is too large.");
var files = await CadZip.ReadAsync(source, args[1], args[2], CancellationToken.None);
source.Position = 0;
var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(source));
Console.WriteLine(JsonSerializer.Serialize(new { fileCount = files.Count, archiveBytes = source.Length, sha256 = digest }));
return 0;
