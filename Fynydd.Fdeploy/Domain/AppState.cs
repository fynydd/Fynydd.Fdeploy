using Spectre.Console;

namespace Fynydd.Fdeploy.Domain;

public sealed class AppState
{
    public string AppOfflineMarkup { get; set; } = string.Empty;
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string YamlCredsFilePath { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string PublishPath { get; set; } = string.Empty;
    public string ProjectBinPath { get; set; } = string.Empty;
    public string ProjectObjPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;

    public string AppleScriptPath => Path.Combine(ProjectPath, "fdeploy_mount.scpt"); 
    
    public Settings Settings { get; set; } = new();
    public List<string> Exceptions { get; set; } = [];
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public StatusContext? CurrentSpinner { get; set; }
    public ConcurrentBag<LocalFileObject> LocalFiles { get; set; } = [];
    public ConcurrentBag<ServerFileObject> ServerFiles { get; set; } = [];
}