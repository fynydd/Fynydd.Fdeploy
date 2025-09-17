using System.Diagnostics.CodeAnalysis;

namespace Fynydd.Fdeploy.Domain;

[SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
public sealed class PathsSettings
{
    public string PublishPath { get; set; } = "bin/publish";

    public List<string> OnlineCopyFolderPaths { get; set; } = [];
    public List<string> OnlineCopyFilePaths { get; set; } = [];

    public List<string> AlwaysOverwritePaths { get; set; } = [];
    public List<string> AlwaysOverwritePathsWithRecurse { get; set; } = [];

    public List<string> IgnoreFolderPaths { get; set; } = [];
    public List<string> IgnoreFilePaths { get; set; } = [];
    public List<string> IgnoreFoldersNamed { get; set; } = [];
    public List<string> IgnoreFilesNamed { get; set; } = [];
}