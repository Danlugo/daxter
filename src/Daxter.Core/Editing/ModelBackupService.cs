using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Export;

namespace Daxter.Core.Editing;

/// <summary>
/// Writes a <c>.bim</c> backup of a model's full definition before a mutation. Because an XMLA write
/// permanently blocks downloading the model as a PBIX, this exported definition is the practical
/// "undo" — keep it. Reuses <see cref="ModelExportService"/> (TOM) and writes to the shared volume.
/// </summary>
public sealed class ModelBackupService
{
    private readonly DaxterConfig _config;
    private readonly XmlaAccessToken _token;

    public ModelBackupService(DaxterConfig config, XmlaAccessToken token)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _token = token;
    }

    /// <summary>Backups directory on the shared volume (honors HOME, the mounted token volume).</summary>
    public static string BackupDir => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "backups");

    /// <summary>Exports the model's <c>.bim</c> and writes it to the backups directory; returns the file path.</summary>
    public string Backup()
    {
        var bim = new ModelExportService(_config, _token).ExportBim();
        Directory.CreateDirectory(BackupDir);
        var name = $"{Sanitize(_config.Workspace)}__{Sanitize(_config.Dataset ?? "model")}__{DateTime.UtcNow:yyyyMMdd-HHmmss}.bim";
        var path = Path.Combine(BackupDir, name);
        File.WriteAllText(path, bim);
        return path;
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
