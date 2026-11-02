namespace XTONE.Services;

public sealed record MusicFileTransfer(string Source, string Destination);

/// <summary>Explicit user-requested filesystem operations. Never replaces existing files.</summary>
public static class MusicFileOrganizer
{
    public static string CreateFolder(string root, string name, string? parent = null)
    {
        var destination = ValidateFolderPath(root, name, parent);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("A file or folder with this name already exists.");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".xtone-folder-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(temporary);
        try { Directory.Move(temporary, destination); }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, false); }
        return destination;
    }

    public static string PrepareFolderRename(string root, string source, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = ValidateDirectory(root, source);
        if (!FolderLibrary.IsWithinRoot(folder, root)) throw new IOException("The Music root cannot be renamed here.");
        var destination = ValidateFolderPath(root, newName, Path.GetDirectoryName(folder));
        if (string.Equals(folder, destination, StringComparison.Ordinal)) return destination;
        if (string.Equals(folder, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose a different name, then rename again to change capitalization.");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("A file or folder with this name already exists.");
        // Reject linked descendants before moving the container, without following them.
        var pending = new Stack<string>(); pending.Push(folder);
        while (pending.TryPop(out var parent))
            foreach (var child in Directory.EnumerateFileSystemEntries(parent))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Folders containing linked files or directories cannot be renamed here.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
            }
        return destination;
    }

    public static string ValidateFolderPath(string root, string name, string? parent = null)
    {
        ValidateFolderName(name);
        var container = ValidateDirectory(root, parent ?? root);
        var destination = Path.Combine(container, name);
        if (File.Exists(destination)) throw new IOException("A file already uses this folder name.");
        if (Directory.Exists(destination)) ValidateDirectory(root, destination);
        return destination;
    }

    public static void ValidateFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255
            || name != name.Trim() || name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Choose a folder name without invalid characters or a trailing dot or space.", nameof(name));
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
            || (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length == 4
               && "123456789¹²³".Contains(stem[3]))
            throw new ArgumentException("This name is reserved by Windows. Choose another name.", nameof(name));
    }

    public static string ValidateDirectory(string root, string directory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && !FolderLibrary.IsWithinRoot(target, root))
            throw new IOException("Choose a destination inside your Music folder.");
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The destination folder is unavailable.");
        RejectReparseAncestors(target);
        return target;
    }

    public static List<MusicFileTransfer> PrepareTransfers(string root, IEnumerable<string> sources, string destination, bool copy)
    {
        var folder = ValidateDirectory(root, destination);
        var transfers = new List<MusicFileTransfer>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!copy && !FolderLibrary.IsWithinRoot(source, root))
                throw new IOException("Only files inside your Music folder can be moved. Use Add tracks to copy external files.");
            if (!File.Exists(source)) throw new FileNotFoundException("A selected audio file is unavailable.", source);
            RejectReparseAncestors(source);
            var target = Path.Combine(folder, Path.GetFileName(source));
            if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            if (!targets.Add(target)) throw new IOException("Two selected files have the same name. Choose them separately or rename them first.");
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException($"{Path.GetFileName(target)} already exists in this folder.");
            transfers.Add(new(source, target));
        }
        return transfers;
    }

    public static void ExecuteMoves(IReadOnlyList<MusicFileTransfer> transfers, Action<int>? progress, CancellationToken token)
    {
        var completed = new List<MusicFileTransfer>();
        try
        {
            for (var i = 0; i < transfers.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = transfers[i]; RejectReparseAncestors(item.Source); RejectReparseAncestors(Path.GetDirectoryName(item.Destination)!);
                File.Move(item.Source, item.Destination, false); completed.Add(item); progress?.Invoke(i + 1);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            try { RollbackMoves(completed); }
            catch (Exception rollback) { throw new AggregateException("The move could not finish or fully return files to their original folders. Check the listed paths.", original, rollback); }
            throw;
        }
    }

    public static void ExecuteCopies(IReadOnlyList<MusicFileTransfer> transfers, Action<int>? progress, CancellationToken token)
    {
        var completed = new List<MusicFileTransfer>();
        try
        {
            for (var i = 0; i < transfers.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var item = transfers[i];
                RejectReparseAncestors(item.Source); RejectReparseAncestors(Path.GetDirectoryName(item.Destination)!);
                // An unsupported temporary suffix keeps watchers from indexing half-copied audio.
                var temporary = item.Destination + ".xtone-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
                    {
                        var buffer = new byte[65536];
                        while (true)
                        {
                            token.ThrowIfCancellationRequested(); var count = source.Read(buffer, 0, buffer.Length);
                            if (count == 0) break;
                            output.Write(buffer, 0, count);
                        }
                        token.ThrowIfCancellationRequested(); output.Flush(true);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, item.Destination, false); completed.Add(item);
                }
                finally { DeleteCreatedFile(temporary); }
                progress?.Invoke(i + 1);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            try { RollbackCopies(completed); }
            catch (Exception rollback) { throw new AggregateException("The copy could not finish or remove every new copy. Original files are unchanged.", original, rollback); }
            throw;
        }
    }

    public static void RollbackMoves(IEnumerable<MusicFileTransfer> transfers)
    {
        var failures = new List<Exception>();
        foreach (var item in transfers.Reverse())
            try { if (File.Exists(item.Destination)) File.Move(item.Destination, item.Source, false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(new IOException($"Could not return {item.Destination} to {item.Source}.", error)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    public static void RollbackCopies(IEnumerable<MusicFileTransfer> transfers)
    {
        var failures = new List<Exception>();
        foreach (var item in transfers.Reverse())
            try { DeleteCreatedFile(item.Destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(new IOException($"Could not remove the new copy {item.Destination}.", error)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrWhiteSpace(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked files and folders cannot be organized here. Choose their physical location.");
    }

    private static void DeleteCreatedFile(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }
}
