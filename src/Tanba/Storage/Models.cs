namespace Tanba.Storage;

public sealed record FileRow(
    long Id,
    string RelPath,
    string OrigName,
    string? Ext,
    long? Size,
    string? Sha256,
    long AddedAt,
    bool Pending,
    /// <summary>
    /// Когда файл последний раз сохраняли. Единственная дата, которая
    /// говорит о самом файле: AddedAt это про программу, а время создания
    /// в Windows это время появления вот этой копии, и на всех файлах
    /// хранилища оно позже времени изменения, потому что копировали их
    /// сюда позже, чем рисовали.
    /// </summary>
    long? Mtime = null);

public sealed record TagRow(
    long Id,
    long GroupId,
    long? ParentId,
    string Name,
    int SortOrder,
    long FileCount);

public sealed record GroupRow(
    long Id,
    string Name,
    bool IsMulti,
    bool IsRequired,
    int SortOrder,
    IReadOnlyList<TagRow> Tags);

/// <summary>Состояние галочки для набора выделенных файлов.</summary>
public enum TagState
{
    Off,      // ни у одного из выделенных
    Partial,  // у части выделенных, тот самый tri-state
    On        // у всех
}

public sealed record IgnoreRule(string Pattern, string Action);

/// <summary>Итог операции «Разложить».</summary>
public sealed record FileResult(
    int Moved,
    int MergedDuplicates,
    int Links,
    int SkippedUntagged,
    List<string> Errors);
