---
module: updates
---
# Модели обновлений

> Загружать: при работе с GitHub auto-update.
> Источник истины: `src/SmartCon.Core/Models/PendingUpdate.cs`, `SemVersion.cs`, `UpdateInfo.cs`, `UpdateSettings.cs`.

## PendingUpdate

Описание подготовленного обновления, ожидающего установки при следующем закрытии Revit.

**Файл:** `SmartCon.Core/Models/PendingUpdate.cs`

```csharp
public sealed record PendingUpdate(
    string Version,
    string StagingPath,
    DateTime StagedAt,
    string TargetInstallPath
);
```

---

## MultiVersionPendingUpdate

Поддерживает несколько версий Revit в одном staged обновлении.

**Файл:** `SmartCon.Core/Models/PendingUpdate.cs`

```csharp
public sealed record MultiVersionPendingUpdate(
    string Version,
    DateTime StagedAt,
    List<StagedArtifact> Artifacts
);
```

---

## StagedArtifact

Один artifact (DLL, .addin) в staged update.

**Файл:** `SmartCon.Core/Models/PendingUpdate.cs`

```csharp
public sealed record StagedArtifact(
    string StagingPath,
    string TargetInstallPath,
    string ArtifactTag
);
```

---

## SemVersion

Лёгкий парсер и компаратор семантических версий (SemVer 2.0.0 subset). Без внешних зависимостей. Поддерживает pre-release метки.

**Файл:** `SmartCon.Core/Models/SemVersion.cs`

```csharp
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? Metadata { get; }
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? metadata = null);
    public static SemVersion Parse(string version);
    public static bool TryParse(string version, [NotNullWhen(true)] out SemVersion? result);
    public int CompareTo(SemVersion? other);
    public override string ToString();
}
```

---

## UpdateInfo

Метаданные GitHub Release, который новее текущей версии плагина.

**Файл:** `SmartCon.Core/Models/UpdateInfo.cs`

```csharp
public sealed record UpdateInfo(
    string Version,
    string TagName,
    string? ReleaseNotes,
    DateTime PublishedAt,
    string DownloadUrl,
    long FileSize,
    string AssetName,
    string? Changelog = null
);
```

---

## UpdateSettings

Пользовательские настройки системы автообновления через GitHub.

**Файл:** `SmartCon.Core/Models/UpdateSettings.cs`

```csharp
public sealed record UpdateSettings(
    bool CheckOnStartup,
    string? GitHubToken,
    string GitHubOwner,
    string GitHubRepo,
    bool IncludePrerelease
)
{
    public static UpdateSettings Default => new(
        CheckOnStartup: true,
        GitHubToken: null,
        GitHubOwner: "Alexandrisius",
        GitHubRepo: "AGK-SmartCon-Pro",
        IncludePrerelease: false
    );
}
```
