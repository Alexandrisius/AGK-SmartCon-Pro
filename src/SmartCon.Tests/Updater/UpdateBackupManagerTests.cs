using System.IO;
using System.Threading;
using SmartCon.Updater;
using Xunit;

namespace SmartCon.Tests.Updater;

public class UpdateBackupManagerTests
{
    [Fact]
    public void CreateBackup_CopiesDirectoryTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartcon-backup-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "2025");
        var backupRoot = Path.Combine(root, "backup");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "a.dll"), "a");
        File.WriteAllText(Path.Combine(target, "sub", "b.dll"), "b");
        try
        {
            var backupDir = UpdateBackupManager.CreateBackup(target, backupRoot, _ => { });

            Assert.NotNull(backupDir);
            Assert.True(File.Exists(Path.Combine(backupDir!, "a.dll")));
            Assert.True(File.Exists(Path.Combine(backupDir!, "sub", "b.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateBackup_MissingTarget_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartcon-backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = UpdateBackupManager.CreateBackup(Path.Combine(root, "missing"), Path.Combine(root, "backup"), _ => { });
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateBackup_PrunesToThreeNewestPerFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartcon-backup-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "2025");
        var backupRoot = Path.Combine(root, "backup");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.dll"), "a");
        try
        {
            for (var i = 0; i < 5; i++)
            {
                UpdateBackupManager.CreateBackup(target, backupRoot, _ => { });
                // гарантируем уникальные timestamp-имена
                foreach (var dir in Directory.GetDirectories(backupRoot))
                {
                    var marker = Path.Combine(dir, $"marker-{i}.txt");
                    if (!File.Exists(marker)) File.WriteAllText(marker, i.ToString());
                }
                Thread.Sleep(1100);
            }

            var backups = Directory.GetDirectories(backupRoot);
            Assert.True(backups.Length <= 3, $"Expected <= 3 backups, got {backups.Length}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
