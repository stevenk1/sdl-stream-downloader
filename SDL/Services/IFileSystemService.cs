namespace SDL.Services;

public interface IFileSystemService
{
    bool FileExists(string? path);
    void FileDelete(string path);
    void FileMove(string source, string destination);
    string FileReadAllText(string path);
    void FileWriteAllText(string path, string contents);
    long GetFileLength(string path);
    
    void CreateDirectory(string path);
    string[] GetFiles(string path, string searchPattern);
    
    string CombinePaths(params string[] paths);
    string? GetFileName(string? path);
    string? GetFileNameWithoutExtension(string? path);
    string GetExtension(string path);
}
