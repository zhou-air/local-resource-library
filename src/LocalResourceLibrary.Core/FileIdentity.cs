namespace LocalResourceLibrary.Core;

/// <summary>Identifies a physical file on a local volume, independently of its path.</summary>
public sealed record FileIdentity(string VolumeId, string FileId);

public interface IFileIdentityProvider
{
    FileIdentity? GetIdentity(string path);
    string? ResolvePath(FileIdentity identity);
}
