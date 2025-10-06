using System.CommandLine;
using DocFxClone;
using Newtonsoft.Json;

// Create root command
var rootCommand = new RootCommand("DocFX Git Cloning Utility - Clone DocFX projects from git repositories with intelligent sparse checkout");

// Create clone command
var cloneCommand = new Command("clone", "Clone a repository and parse DocFX project with sparse checkout");
var cloneRepoUrlArg = new Argument<string>("repo-url", "The git repository URL (e.g., https://github.com/user/repo.git)");
var cloneDocfxPathArg = new Argument<string>("docfx-path", "Path to docfx.json within the repository (e.g., docs/docfx.json)");
var cloneOutputOption = new Option<string?>("--output", "Output directory (default: ./<repo-name>)");
var cloneBranchOption = new Option<string?>("--branch", "Branch to clone (default: remote HEAD)");
var cloneSilentOption = new Option<bool>("--silent", "Suppress progress output");

cloneCommand.AddArgument(cloneRepoUrlArg);
cloneCommand.AddArgument(cloneDocfxPathArg);
cloneCommand.AddOption(cloneOutputOption);
cloneCommand.AddOption(cloneBranchOption);
cloneCommand.AddOption(cloneSilentOption);

cloneCommand.SetHandler(async (string repoUrl, string docfxPath, string? outputPath, string? branch, bool silent) =>
{
    try
    {
        // Default output path
        if (string.IsNullOrEmpty(outputPath))
        {
            var repoName = Path.GetFileNameWithoutExtension(new Uri(repoUrl).AbsolutePath.TrimEnd('/'));
            outputPath = Path.Combine(Directory.GetCurrentDirectory(), repoName);
        }

        IGitOperationCallback callback = silent ? new SilentGitOperationCallback() : new ConsoleGitOperationCallback();
        var gitUtility = new GitCloningUtility(outputPath, callback);
        var integration = new DocFxGitIntegration(gitUtility, callback);

        if (!silent)
        {
            Console.WriteLine($"Cloning repository: {repoUrl}");
            Console.WriteLine($"DocFX config: {docfxPath}");
            Console.WriteLine($"Output directory: {outputPath}");
            if (!string.IsNullOrEmpty(branch))
            {
                Console.WriteLine($"Branch: {branch}");
            }
            Console.WriteLine();
        }

        var result = await integration.CloneAndParseAsync(repoUrl, docfxPath, branch);

        if (!silent)
        {
            Console.WriteLine();
            Console.WriteLine($"Successfully cloned and parsed DocFX project");
            Console.WriteLine($"Total files: {result.Files.Count}");
            Console.WriteLine($"Checked out files: {gitUtility.GetCheckedOutFiles().Count}");
        }

        // Output JSON result
        var json = JsonConvert.SerializeObject(result, Formatting.Indented);
        Console.WriteLine();
        Console.WriteLine(json);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Details: {ex.InnerException.Message}");
        }
        Environment.Exit(2);
    }
}, cloneRepoUrlArg, cloneDocfxPathArg, cloneOutputOption, cloneBranchOption, cloneSilentOption);

// Create parse command
var parseCommand = new Command("parse", "Parse an existing repository with on-demand file checkout");
var parseLocalRepoArg = new Argument<string>("local-repo-path", "Path to the local repository");
var parseDocfxPathArg = new Argument<string>("docfx-path", "Path to docfx.json within the repository");
var parseSilentOption = new Option<bool>("--silent", "Suppress progress output");

parseCommand.AddArgument(parseLocalRepoArg);
parseCommand.AddArgument(parseDocfxPathArg);
parseCommand.AddOption(parseSilentOption);

parseCommand.SetHandler(async (string localRepoPath, string docfxPath, bool silent) =>
{
    try
    {
        if (!Directory.Exists(localRepoPath))
        {
            Console.Error.WriteLine($"Directory not found: {localRepoPath}");
            Environment.Exit(1);
        }

        IGitOperationCallback callback = silent ? new SilentGitOperationCallback() : new ConsoleGitOperationCallback();
        var gitUtility = new GitCloningUtility(localRepoPath, callback);
        var integration = new DocFxGitIntegration(gitUtility, callback);

        if (!silent)
        {
            Console.WriteLine($"Parsing DocFX project: {docfxPath}");
            Console.WriteLine($"Repository directory: {localRepoPath}");
            Console.WriteLine();
        }

        var result = await integration.ParseWithCheckoutAsync(docfxPath);

        if (!silent)
        {
            Console.WriteLine();
            Console.WriteLine($"Successfully parsed DocFX project");
            Console.WriteLine($"Total files: {result.Files.Count}");
            Console.WriteLine($"Checked out files: {gitUtility.GetCheckedOutFiles().Count}");
        }

        // Output JSON result
        var json = JsonConvert.SerializeObject(result, Formatting.Indented);
        Console.WriteLine();
        Console.WriteLine(json);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Details: {ex.InnerException.Message}");
        }
        Environment.Exit(2);
    }
}, parseLocalRepoArg, parseDocfxPathArg, parseSilentOption);

// Add commands to root
rootCommand.AddCommand(cloneCommand);
rootCommand.AddCommand(parseCommand);

// Execute
return await rootCommand.InvokeAsync(args);
