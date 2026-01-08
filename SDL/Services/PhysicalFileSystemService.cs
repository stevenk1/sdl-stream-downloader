namespace SDL.Services;

public class PhysicalFileSystemService : IFileSystemService
{
    public bool FileExists(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    public void FileDelete(string path) => File.Delete(path);

    public void FileMove(string source, string destination) => File.Move(source, destination);

    public string FileReadAllText(string path) => File.ReadAllText(path);

    public void FileWriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string[] GetFiles(string path, string searchPattern) => Directory.GetFiles(path, searchPattern);

    public string CombinePaths(params string[] paths) => Path.Combine(paths);

    public string? GetFileName(string? path) => path != null ? Path.GetFileName(path) : null;

    public string? GetFileNameWithoutExtension(string? path) => path != null ? Path.GetFileNameWithoutExtension(path) : null;

    public string GetExtension(string path) => Path.GetExtension(path);
}
