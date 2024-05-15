using Fynydd.Fdeploy.ConsoleBusy;
using SMBLibrary.Client;
using YamlDotNet.Serialization;

namespace Fynydd.Fdeploy.Domain;

public sealed class AppRunner
{
    #region Constants

    public static int MaxConsoleWidth => GetMaxConsoleWidth();
	
    private static int GetMaxConsoleWidth()
    {
        try
        {
            return Console.WindowWidth - 1;
        }
        catch
        {
            return 78;
        }
    }

    public static string CliErrorPrefix => "  • ";

    #endregion

    #region Run Mode Properties

    public bool VersionMode { get; set; }
    public bool InitMode { get; set; }
    public bool HelpMode { get; set; }

    #endregion
    
    #region App State Properties

    public List<string> CliArguments { get; } = [];
    public AppState AppState { get; } = new();

    #endregion
    
    #region Properties
    
    public Stopwatch Timer { get; } = new();

    #endregion
    
    public AppRunner(IEnumerable<string> args)
    {
        #region Process Arguments
        
        CliArguments.AddRange(args);

        if (CliArguments.Count == 0)
            AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml");
        
        if (CliArguments.Count == 1)
        {
            if (CliArguments[0] == "help")
            {
                HelpMode = true;
                return;
            }

            if (CliArguments[0] == "version")
            {
                VersionMode = true;
                return;
            }

            if (CliArguments[0] == "init")
            {
                InitMode = true;
                return;
            }

            var projectFilePath = CliArguments[0].SetNativePathSeparators();

            if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && projectFilePath.Contains(Path.DirectorySeparatorChar))
            {
                AppState.YamlProjectFilePath = projectFilePath;
            }
            else
            {
                if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), projectFilePath);
                else
                    AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), $"fdeploy-{projectFilePath}.yml");
            }
        }

        if (File.Exists(AppState.YamlProjectFilePath) == false)
        {
            HelpMode = true;
            return;
        }

        #endregion
        
        #region Load Settings
        
        if (File.Exists(AppState.YamlProjectFilePath) == false)
        {
            AppState.Exceptions.Add($"Could not find project file `{AppState.YamlProjectFilePath}`");
            AppState.CancellationTokenSource.Cancel();
            return;
        }

        if (AppState.YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            AppState.Exceptions.Add($"Invalid project file path `{AppState.YamlProjectFilePath}`");
            AppState.CancellationTokenSource.Cancel();
            return;
        }
            
        AppState.ProjectPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(AppState.YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        AppState.Settings = deserializer.Deserialize<Settings>(yaml);

        if (AppState.Settings.MaxThreadCount < 1)
            AppState.Settings.MaxThreadCount = new Settings().MaxThreadCount;
        
        AppState.ProjectPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];

        #region Credentials
        
        AppState.YamlCredsFilePath = Path.Combine(AppState.ProjectPath, "fdeploy-creds.yml");

        if (File.Exists(AppState.YamlCredsFilePath) == false)
        {
            AppState.YamlCredsFilePath = Path.Combine(AppState.ProjectPath, AppState.YamlProjectFilePath.GetLastPathSegment().Replace(".yml", "-creds.yml"));

            if (File.Exists(AppState.YamlCredsFilePath) == false)
            {
                AppState.YamlCredsFilePath = string.Empty;
            }
        }        

        if (AppState.YamlCredsFilePath != string.Empty)
        {
            yaml = File.ReadAllText(AppState.YamlCredsFilePath);
            
            var credentials = deserializer.Deserialize<Credentials>(yaml);

            AppState.Settings.ServerConnection.Domain = credentials.Domain;
            AppState.Settings.ServerConnection.UserName = credentials.UserName;
            AppState.Settings.ServerConnection.Password = credentials.Password;
        }        

        #endregion
        
        #endregion

        #region Normalize Paths

        AppState.PublishPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        AppState.ProjectBinPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}bin";
        AppState.ProjectObjPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}obj";
        AppState.TrimmablePublishPath = AppState.PublishPath.TrimPath();

        AppState.Settings.Project.ProjectFilePath = AppState.Settings.Project.ProjectFilePath.NormalizePath();
        AppState.Settings.ServerConnection.RemoteRootPath = AppState.Settings.ServerConnection.RemoteRootPath.NormalizeSmbPath();

        AppState.Settings.Project.CopyFilesToPublishFolder.NormalizePaths();
        AppState.Settings.Project.CopyFoldersToPublishFolder.NormalizePaths();

        AppState.Settings.Paths.OnlineCopyFilePaths.NormalizePaths();
        AppState.Settings.Paths.OnlineCopyFolderPaths.NormalizePaths();

        AppState.Settings.Paths.AlwaysOverwritePaths.NormalizePaths();
        AppState.Settings.Paths.AlwaysOverwritePathsWithRecurse.NormalizePaths();

        AppState.Settings.Paths.IgnoreFilePaths.NormalizePaths();
        AppState.Settings.Paths.IgnoreFolderPaths.NormalizePaths();

        var newList = new List<string>();
        
        foreach (var item in AppState.Settings.Project.CopyFilesToPublishFolder)
            newList.Add(item.NormalizePath().TrimStart(AppState.ProjectPath).TrimPath());

        AppState.Settings.Project.CopyFilesToPublishFolder.Clear();
        AppState.Settings.Project.CopyFilesToPublishFolder.AddRange(newList);

        newList.Clear();
        
        foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
            newList.Add(item.NormalizePath().TrimStart(AppState.ProjectPath).TrimPath());

        AppState.Settings.Project.CopyFoldersToPublishFolder.Clear();
        AppState.Settings.Project.CopyFoldersToPublishFolder.AddRange(newList);

        #endregion
    }
    
    #region Embedded Resources 
    
    public async ValueTask<string> GetEmbeddedYamlPathAsync()
    {
        var workingPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

        while (workingPath.LastIndexOf(Path.DirectorySeparatorChar) > -1)
        {
            workingPath = workingPath[..workingPath.LastIndexOf(Path.DirectorySeparatorChar)];
            
#if DEBUG
            if (Directory.Exists(Path.Combine(workingPath, "yaml")) == false)
                continue;

            var tempPath = workingPath; 
			
            workingPath = Path.Combine(tempPath, "yaml");
#else
			if (Directory.Exists(Path.Combine(workingPath, "contentFiles")) == false)
				continue;
		
			var tempPath = workingPath; 

			workingPath = Path.Combine(tempPath, "contentFiles", "any", "any", "yaml");
#endif
            break;
        }

        // ReSharper disable once InvertIf
        if (string.IsNullOrEmpty(workingPath) || Directory.Exists(workingPath) == false)
        {
            AppState.Exceptions.Add("Embedded YAML resources cannot be found.");
            await AppState.CancellationTokenSource.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    public async ValueTask<string> GetEmbeddedHtmlPathAsync()
    {
        var workingPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

        while (workingPath.LastIndexOf(Path.DirectorySeparatorChar) > -1)
        {
            workingPath = workingPath[..workingPath.LastIndexOf(Path.DirectorySeparatorChar)];
            
#if DEBUG
            if (Directory.Exists(Path.Combine(workingPath, "html")) == false)
                continue;

            var tempPath = workingPath; 
			
            workingPath = Path.Combine(tempPath, "html");
#else
			if (Directory.Exists(Path.Combine(workingPath, "contentFiles")) == false)
				continue;
		
			var tempPath = workingPath; 

			workingPath = Path.Combine(tempPath, "contentFiles", "any", "any", "html");
#endif
            break;
        }

        // ReSharper disable once InvertIf
        if (string.IsNullOrEmpty(workingPath) || Directory.Exists(workingPath) == false)
        {
            AppState.Exceptions.Add("Embedded HTML resources cannot be found.");
            await AppState.CancellationTokenSource.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    #endregion
    
    #region Console Output
    
    public async ValueTask OutputExceptionsAsync()
    {
        foreach (var message in AppState.Exceptions)
            await Console.Out.WriteLineAsync($"{CliErrorPrefix}{message}");
    }

    public static async ValueTask ColonOutAsync(string topic, string message)
    {
        const int maxTopicLength = 20;

        if (topic.Length >= maxTopicLength)
            await Console.Out.WriteAsync($"{topic[..maxTopicLength]}");
        else
            await Console.Out.WriteAsync($"{topic}{" ".Repeat(maxTopicLength - topic.Length)}");
        
        await Console.Out.WriteLineAsync($" : {message}");
    }
    
    #endregion
    
    #region Deployment
   
    public async ValueTask DeployAsync()
    {
        #region Process Modes
        
		var version = await Identify.VersionAsync(System.Reflection.Assembly.GetExecutingAssembly());
        
		if (VersionMode)
		{
			await Console.Out.WriteLineAsync($"Fdeploy Version {version}");
			return;
		}
		
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		await Console.Out.WriteLineAsync("Fdeploy: Deploy .NET web applications using SMB on Linux, macOS, or Windows");
		await Console.Out.WriteLineAsync($"Version {version} for {Identify.GetOsPlatformName()} (.NET {Identify.GetRuntimeVersion()}/{Identify.GetProcessorArchitecture()})");
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		
		if (InitMode)
		{
			var yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(), "fdeploy.yml"), AppState.CancellationTokenSource.Token);

            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, AppState.CancellationTokenSource.Token);
			
            yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(), "fdeploy-creds.yml"), AppState.CancellationTokenSource.Token);

            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy-creds.yml"), yaml, AppState.CancellationTokenSource.Token);
            
            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
            {
			    await Console.Out.WriteLineAsync($"Created `fdeploy.yml` and `fdeploy-creds.yml` at {Directory.GetCurrentDirectory()}");
			    await Console.Out.WriteLineAsync();
    			return;
            }
		}
		
		else if (HelpMode)
		{
			await Console.Out.WriteLineAsync();
            
            const string helpText = """
                                    Fdeploy will look in the current working directory for a deployment file named `fdeploy.yml`._
                                    You can also pass a path to a file named `fdeploy-{name}.yml` or even just pass the `{name}` portion which will look for a file named `fdeploy-{name}.yml`.

                                    Although you can put server credentials in the deployment file under the ServerConnection section, it is recommended that you instead use a separate credentials file and exclude it from your source code repository (e.g. git)._
                                    The name of the file can be either `fdeploy-creds.yml` which is used for all deployment files in a given project folder, or use a deployment filename with `-creds` at the end (e.g. `fdeploy-staging-creds.yml`).

                                    Command Line Usage:
                                    """;

            const string exampleText = """
                                       fdeploy [init|help|version]
                                       fdeploy
                                       fdeploy {path to fdeploy-{name}.yml file}
                                       fdeploy {name}

                                       Commands:
                                       """;

            const string commandsText = """
                                        init      : Create starter `fdeploy.yml` and `fdeploy-creds.yml` files in the
                                                  : current working directory
                                        version   : Show the Fdeploy version number
                                        help      : Show this help message
                                        """;

            helpText.WriteToConsole(80);
            await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Command Line Usage:".Length));
            exampleText.WriteToConsole(80);
            await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Commands:".Length));
            commandsText.WriteToConsole(80);
            
			await Console.Out.WriteLineAsync();

			return;
		}

        await ColonOutAsync("Destination", $@"\\{AppState.Settings.ServerConnection.ServerAddress}\{AppState.Settings.ServerConnection.ShareName}\{AppState.Settings.ServerConnection.RemoteRootPath}");
		await ColonOutAsync("Settings File", AppState.YamlProjectFilePath);
        
        if (AppState.YamlCredsFilePath != string.Empty)
            await ColonOutAsync("Credentials File", AppState.YamlCredsFilePath);
        
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion

        AppState.AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(), "AppOffline.html"), AppState.CancellationTokenSource.Token);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{MetaTitle}}", AppState.Settings.Offline.MetaTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageTitle}}", AppState.Settings.Offline.PageTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageHtml}}", AppState.Settings.Offline.ContentHtml);

        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();

        SMB2Client? client = null;
        var sb = new StringBuilder();
        
        #region Delete Publish Folder

        if (Directory.Exists(AppState.PublishPath))
        {
            await Spinner.StartAsync("Delete existing publish folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var retries = AppState.Settings.RetryCount;

                if (retries < 0)
                    retries = new Settings().RetryCount;

                for (var x = 0; x < retries; x++)
                {
                    try
                    {
                        Directory.Delete(AppState.PublishPath, true);
                        break;
                    }
                    catch
                    {
                        spinner.Text = $"{spinnerText} Retry {x + 1}";
                        await Task.Delay(AppState.Settings.WriteRetryDelaySeconds * 1000);
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                    return;
                }

                spinner.Text = $"{spinnerText} Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion

        #region Clean Project

        if (AppState.Settings.CleanProject)
        {
            await Spinner.StartAsync($"Clean project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
            {
                try
                {
                    var spinnerText = spinner.Text;

                    Timer.Restart();

                    var cmd = Cli.Wrap("dotnet")
                        .WithArguments(new [] { "clean", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}" })
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                        .WithStandardErrorPipe(PipeTarget.Null);
                    
                    var result = await cmd.ExecuteAsync();

                    if (result.IsSuccess == false)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                        AppState.Exceptions.Add($"Could not clean the project; exit code: {result.ExitCode}");
                        await AppState.CancellationTokenSource.CancelAsync();
                        return;
                    }
                    
                    spinner.Text = $"Clean project ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }

                catch (Exception e)
                {
                    spinner.Fail($"Cleaning project {AppState.Settings.Project.ProjectFileName}... Failed!");
                    AppState.Exceptions.Add($"Could not clean the project; {e.Message}");
                    await AppState.CancellationTokenSource.CancelAsync();
                }
                
            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }

        #endregion

        #region Purge Bin Folder

        if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectBinPath))
        {
            await Spinner.StartAsync("Purge bin folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var retries = AppState.Settings.RetryCount;

                if (retries < 0)
                    retries = new Settings().RetryCount;

                for (var x = 0; x < retries; x++)
                {
                    try
                    {
                        Directory.Delete(AppState.ProjectBinPath, true);
                        Directory.CreateDirectory(AppState.ProjectBinPath);
                        break;
                    }
                    catch
                    {
                        spinner.Text = $"{spinnerText} Retry {x + 1}";
                        await Task.Delay(AppState.Settings.WriteRetryDelaySeconds * 1000);
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                    return;
                }

                spinner.Text = $"{spinnerText} Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion

        #region Purge Obj Folder

        if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectObjPath))
        {
            await Spinner.StartAsync("Purge obj folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var retries = AppState.Settings.RetryCount;

                if (retries < 0)
                    retries = new Settings().RetryCount;

                for (var x = 0; x < retries; x++)
                {
                    try
                    {
                        Directory.Delete(AppState.ProjectObjPath, true);
                        Directory.CreateDirectory(AppState.ProjectObjPath);
                        break;
                    }
                    catch
                    {
                        spinner.Text = $"{spinnerText} Retry {x + 1}";
                        await Task.Delay(AppState.Settings.WriteRetryDelaySeconds * 1000);
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                    return;
                }

                spinner.Text = $"{spinnerText} Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion
        
        #region Publish Project
        
        await Spinner.StartAsync($"Publishing project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var spinnerText = spinner.Text;

                Timer.Restart();

                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                    AppState.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await AppState.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"Publishing project {AppState.Settings.Project.ProjectFileName}... Failed!");
                AppState.Exceptions.Add($"Could not publish the project; {e.Message}");
                await AppState.CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        //await Storage.CopyFolderAsync(AppState, Path.Combine(AppState.WorkingPath, "wwwroot", "media"), Path.Combine(AppState.PublishPath, "wwwroot", "media"));
        
        #endregion
        
        #region Copy Additional Files Into Publish Folder

        if (AppState.Settings.Project.CopyFilesToPublishFolder.Count != 0)
        {
            await Spinner.StartAsync("Adding files to publish folder...", async spinner =>
            {
                var spinnerText = spinner.Text;

                Timer.Restart();
                
                foreach (var item in AppState.Settings.Project.CopyFilesToPublishFolder)
                {
                    var sourceFilePath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}";
                    var destFilePath = $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}";
                    var destParentPath = destFilePath.TrimEnd(item.GetLastPathSegment()) ?? string.Empty;
                    
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        Timer.Restart();

                        if (Directory.Exists(destParentPath) == false)
                            Directory.CreateDirectory(destParentPath);
                        
                        File.Copy(sourceFilePath, destFilePath, true);
                        
                        spinner.Text = $"{spinnerText} {item.GetLastPathSegment()}...";

                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinnerText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add file `{sourceFilePath} => {destFilePath}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinnerText} {AppState.Settings.Project.CopyFilesToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFilesToPublishFolder.Count.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Copy Additional Folders Into Publish Folder
        
        if (AppState.Settings.Project.CopyFoldersToPublishFolder.Count != 0)
        {
            await Spinner.StartAsync("Adding folders to publish folder...", async spinner =>
            {
                var spinnerText = spinner.Text;

                Timer.Restart();
                
                foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
                {
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        Timer.Restart();

                        await Storage.CopyLocalFolderAsync(AppState, $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}", $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}");

                        spinner.Text = $"{spinnerText} {item.GetLastPathSegment()}...";
                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinnerText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add folder `{item}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinnerText} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count.Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            var spinnerText = spinner.Text;

            Timer.Restart();
            
            await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail($"{spinnerText} Failed!");
            else        
                spinner.Text = $"{spinnerText} {AppState.LocalFiles.Count(f => f.IsFile):N0} {AppState.LocalFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.LocalFiles.Count(f => f.IsFolder):N0} {AppState.LocalFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        try
        {
            #region Verify Server Connection And Share

            await Spinner.StartAsync("Connecting to server...", async spinner =>
            {
                var spinnerText = spinner.Text;

                AppState.CurrentSpinner = spinner;

                client = Storage.CreateClient(AppState, true, false);

                if (client is not null && AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Succeed($"{spinnerText} Success!");

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Index Server Files

            await Spinner.StartAsync("Indexing server files...", async spinner =>
            {
                var spinnerText = spinner.Text;

                AppState.CurrentSpinner = spinner;

                Timer.Restart();

                await Storage.RecurseServerPathAsync(AppState, AppState.Settings.ServerConnection.RemoteRootPath);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text = $"{spinnerText} {AppState.ServerFiles.Count(f => f.IsFile):N0} {AppState.ServerFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.ServerFiles.Count(f => f.IsFolder):N0} {AppState.ServerFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Create New Folders
            
            var foldersToCreate = new List<string>();

            await Spinner.StartAsync("Create new folders...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;

                foreach (var folder in AppState.LocalFiles.Where(f => f is { IsFolder: true }).ToList())
                {
                    if (AppState.ServerFiles.Any(f => f.IsFolder && f.RelativeComparablePath == folder.RelativeComparablePath) == false)
                        foldersToCreate.Add($"{AppState.Settings.ServerConnection.RemoteRootPath}\\{folder.RelativeComparablePath.SetSmbPathSeparators().TrimPath()}");
                }

                if (foldersToCreate.Count > 0)
                {
                    var fileStore = client.GetFileStore(AppState);

                    try
                    {
                        if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                            return;

                        foreach (var folder in foldersToCreate.OrderBy(f => f))
                        {
                            client.EnsureServerPathExists(fileStore, AppState, folder);
                        }
                    }

                    finally
                    {
                        Storage.DisconnectFileStore(fileStore);
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {foldersToCreate.Count:N0} {foldersToCreate.Count.Pluralize("folder", "folders")} created ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }

                else
                {
                    spinner.Text = $"{spinnerText} No new folders... Success!";
                }
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
            
            #endregion

            #region Deploy Files While Online

            if (AppState.Settings.Paths.OnlineCopyFolderPaths.Count > 0 || AppState.Settings.Paths.OnlineCopyFilePaths.Count > 0)
            {
                await Spinner.StartAsync("Deploy files (server online)...", async spinner =>
                {
                    var spinnerText = spinner.Text;
                    var filesCopied = 0;

                    AppState.CurrentSpinner = spinner;
                    Timer.Restart();

                    var filesToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsOnlineCopy: true }).OrderBy(f => f.RelativeComparablePath).ToList();

                    const int groupSize = 10;

                    // Threads of `groupSize` items to copy concurrently
                    var arrayOfLists = filesToCopy
                        .Select((item, index) => new { Item = item, Index = index })
                        .GroupBy(x => x.Index / groupSize)
                        .Select(g => g.Select(x => x.Item).ToList())
                        .ToArray();
                    
                    var tasks = new List<Task>();
                    var semaphore = new SemaphoreSlim(AppState.Settings.MaxThreadCount);

                    foreach (var group in arrayOfLists)
                    {
                        await semaphore.WaitAsync();

                        tasks.Add(Task.Run(() =>
                        {
                            SMB2Client? innerClient = null;
                            ISMBFileStore? innerFileStore = null;

                            try
                            {
                                if (AppState.CancellationTokenSource.IsCancellationRequested)
                                    return;

                                innerClient = Storage.CreateClient(AppState);
                    
                                if (innerClient is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                {
                                    AppState.Exceptions.Add("Could not establish Client when copying online file group");
                                    AppState.CancellationTokenSource.Cancel();
                                    return;
                                }

                                innerFileStore = innerClient.GetFileStore(AppState);

                                if (innerFileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                {
                                    AppState.Exceptions.Add("Could not establish FileStore when copying online file group");
                                    AppState.CancellationTokenSource.Cancel();
                                    return;
                                }
                        
                                foreach (var fo in group)
                                {
                                    var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == fo.RelativeComparablePath && f.IsDeleted == false);

                                    if (fo.AlwaysOverwrite == false && serverFile is not null && (AppState.Settings.CompareFileDates == false || serverFile.LastWriteTime == fo.LastWriteTime) && (AppState.Settings.CompareFileSizes == false || serverFile.FileSizeBytes == fo.FileSizeBytes))
                                        continue;

                                    spinner.Text = $"{spinnerText} {fo.FileNameOrPathSegment}...";
                                    innerClient.CopyFile(innerFileStore, AppState, fo);
                                    filesCopied++;
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                                Storage.DisconnectFileStore(innerFileStore);
                                Storage.DisconnectClient(innerClient);
                            }
                        }));
                    }

                    await Task.WhenAll(tasks);
                    
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                    }
                    else
                    {
                        if (filesCopied != 0)
                            spinner.Text = $"{spinnerText} {AppState.Settings.Paths.OnlineCopyFolderPaths.Count:N0} {AppState.Settings.Paths.OnlineCopyFolderPaths.Count.Pluralize("folder", "folders")} with {filesCopied:N0} {filesCopied.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                        else
                            spinner.Text = $"{spinnerText} Nothing to copy... Success!";
                    }

                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Take Server Offline

            var offlineTimer = new Stopwatch();

            if (AppState.Settings.TakeServerOffline)
            {
                ISMBFileStore? fileStore = null;
                
                try
                {
                    fileStore = client.GetFileStore(AppState);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    offlineTimer.Start();

                    await Spinner.StartAsync("Take website offline...", async spinner =>
                    {
                        AppState.CurrentSpinner = spinner;

                        var spinnerText = spinner.Text;

                        client.TakeServerOffline(fileStore, AppState);

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                        {
                            spinner.Fail($"{spinnerText} Failed!");
                        }
                        else
                        {
                            if (AppState.Settings.ServerOfflineDelaySeconds > 0)
                            {
                                for (var i = AppState.Settings.ServerOfflineDelaySeconds; i >= 0; i--)
                                {
                                    spinner.Text = $"{spinnerText} Done... Waiting ({i:N0})";

                                    await Task.Delay(1000);
                                }
                            }

                            spinner.Text = $"{spinnerText} Success!";
                        }

                    }, Patterns.Dots, Patterns.Line);

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        return;
                }
                finally    
                {
                    Storage.DisconnectFileStore(fileStore);
                }
            }

            #endregion

            #region Deploy Files While Offline

            Timer.Restart();
            
            var filesToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsOnlineCopy: false }).OrderBy(f => f.RelativeComparablePath).ToList();
            var fileCount = filesToCopy.Count;

            await Spinner.StartAsync("Deploy files (server offline)...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var filesCopied = 0;

                if (fileCount > 0)
                {
                    const int groupSize = 10;

                    // Threads of `groupSize` items to copy concurrently
                    var arrayOfLists = filesToCopy
                        .Select((item, index) => new { Item = item, Index = index })
                        .GroupBy(x => x.Index / groupSize)
                        .Select(g => g.Select(x => x.Item).ToList())
                        .ToArray();

                    var tasks = new List<Task>();
                    var semaphore = new SemaphoreSlim(AppState.Settings.MaxThreadCount);
                    
                    foreach (var group in arrayOfLists)
                    {
                        await semaphore.WaitAsync();
                        
                        tasks.Add(Task.Run(() =>
                        {
                            SMB2Client? innerClient = null;
                            ISMBFileStore? innerFileStore = null;

                            try
                            {
                                if (AppState.CancellationTokenSource.IsCancellationRequested)
                                    return;

                                innerClient = Storage.CreateClient(AppState);
                        
                                if (innerClient is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                {
                                    AppState.Exceptions.Add("Could not establish Client when copying online file group");
                                    AppState.CancellationTokenSource.Cancel();
                                    return;
                                }

                                innerFileStore = innerClient.GetFileStore(AppState);

                                if (innerFileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                {
                                    AppState.Exceptions.Add("Could not establish FileStore when copying online file group");
                                    AppState.CancellationTokenSource.Cancel();
                                    return;
                                }
                            
                                foreach (var fo in group)
                                {
                                    var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == fo.RelativeComparablePath && f.IsDeleted == false);

                                    if (fo.AlwaysOverwrite == false && serverFile is not null && (AppState.Settings.CompareFileDates == false || serverFile.LastWriteTime == fo.LastWriteTime) && (AppState.Settings.CompareFileSizes == false || serverFile.FileSizeBytes == fo.FileSizeBytes))
                                        continue;
                               
                                    innerClient.CopyFile(innerFileStore, AppState, fo);

                                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                                        return;

                                    filesCopied++;
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                                Storage.DisconnectFileStore(innerFileStore);
                                Storage.DisconnectClient(innerClient);
                            }
                        }));
                    }

                    await Task.WhenAll(tasks);

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} updated ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }
                else
                {
                    spinner.Text = $"{spinnerText} no files to update... Success!";
                }

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Process Deletions

            if (AppState.Settings.DeleteOrphans)
            {
                ISMBFileStore? fileStore = null;

                try
                {
                    fileStore = client.GetFileStore(AppState);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    await Spinner.StartAsync("Deleting orphaned files and folders...", async spinner =>
                    {
                        var spinnerText = spinner.Text;
                        var filesRemoved = 0;
                        var foldersRemoved = 0;

                        AppState.CurrentSpinner = spinner;
                        
                        Timer.Restart();

                        var itemsToDelete = AppState.ServerFiles.Except(AppState.LocalFiles, new FileObjectComparer()).Where(f => f.IsDeleted == false).ToList();

                        // Remove paths that enclose ignore paths
                        foreach (var item in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                        {
                            foreach (var ignorePath in AppState.Settings.Paths.IgnoreFolderPaths)
                            {
                                if (ignorePath.StartsWith(item.RelativeComparablePath) == false)
                                    continue;

                                itemsToDelete.Remove(item);
                            }
                        }

                        // Remove descendants of folders to be deleted
                        foreach (var item in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                        {
                            foreach (var subitem in itemsToDelete.ToList().OrderByDescending(o => o.Level))
                            {
                                if (subitem.Level > item.Level && subitem.RelativeComparablePath.StartsWith(item.RelativeComparablePath))
                                    itemsToDelete.Remove(subitem);
                            }
                        }

                        foreach (var item in itemsToDelete)
                        {
                            if (item.IsFile)
                            {
                                client.DeleteServerFile(fileStore, AppState, item);
                                filesRemoved++;
                            }
                            else
                            {
                                client.DeleteServerFolderRecursive(fileStore, AppState, item);
                                foldersRemoved++;
                            }

                            if (AppState.CancellationTokenSource.IsCancellationRequested)
                                break;
                        }

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                            spinner.Fail($"{spinnerText} Failed!");
                        else
                            spinner.Text = $"Deleted {filesRemoved:N0} orphaned {filesRemoved.Pluralize("file", "files")} and {foldersRemoved:N0} {foldersRemoved.Pluralize("folder", "folders")} of orphaned files ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                        await Task.CompletedTask;

                    }, Patterns.Dots, Patterns.Line);

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        return;
                }
                finally    
                {
                    Storage.DisconnectFileStore(fileStore);
                }
            }

            #endregion
            
            #region Bring Server Online

            if (AppState.Settings.TakeServerOffline)
            {
                await Spinner.StartAsync("Bring website online...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    ISMBFileStore? fileStore = null;

                    try
                    {
                        var spinnerText = spinner.Text;
                        
                        fileStore = client.GetFileStore(AppState);

                        if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                            return;

                        if (AppState.Settings.ServerOnlineDelaySeconds > 0)
                        {
                            for (var i = AppState.Settings.ServerOnlineDelaySeconds; i >= 0; i--)
                            {
                                spinner.Text = $"{spinnerText}... Waiting ({i:N0})";
                                await Task.Delay(1000);
                            }
                        }

                        client.BringServerOnline(fileStore, AppState);
                        
                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                        {
                            spinner.Fail($"{spinnerText} Failed!");
                        }
                        else
                        {
                            spinner.Text = $"{spinnerText} Success!";
                        }
                    }
                    finally    
                    {
                        Storage.DisconnectFileStore(fileStore);
                    }

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;

                await Spinner.StartAsync("Website offline for ", async spinner =>
                {
                    spinner.Text += offlineTimer.Elapsed.FormatElapsedTime();

                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);
            }

            #endregion

            #region Local Cleanup

            await Spinner.StartAsync("Cleaning up...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var retries = AppState.Settings.RetryCount;

                if (retries < 0)
                    retries = new Settings().RetryCount;

                for (var x = 0; x < retries; x++)
                {
                    try
                    {
                        Directory.Delete(AppState.PublishPath, true);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(AppState.Settings.WriteRetryDelaySeconds * 1000);
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text = $"{spinnerText} Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            #endregion
        }
        finally
        {
            Storage.DisconnectClient(client);
        }
    }
    
    #endregion
}