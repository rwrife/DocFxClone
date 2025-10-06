using Newtonsoft.Json;

namespace DocFxClone;

/// <summary>
/// Command-line interface for the git cloning utility.
/// </summary>
public static class GitCloningCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "clone" => await HandleCloneCommandAsync(args),
                "parse" => await HandleParseCommandAsync(args),
                _ => HandleUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"Details: {ex.InnerException.Message}");
            }
            return 2;
        }
    }

    private static async Task<int> HandleCloneCommandAsync(string[] args)
    {
        // clone <repo-url> <docfx-path> [--output <path>] [--branch <branch>] [--silent]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: clone <repo-url> <docfx-path> [--output <path>] [--branch <branch>] [--silent]");
            return 1;
        }

        var repoUrl = args[1];
        var docfxPath = args[2];
        string? outputPath = null;
        string? branch = null;
        bool silent = false;

        // Parse optional arguments
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                outputPath = args[++i];
            }
            else if (args[i] == "--branch" && i + 1 < args.Length)
            {
                branch = args[++i];
            }
            else if (args[i] == "--silent")
            {
                silent = true;
            }
        }

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

        return 0;
    }

    private static async Task<int> HandleParseCommandAsync(string[] args)
    {
        // parse <local-repo-path> <docfx-path> [--silent]
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: parse <local-repo-path> <docfx-path> [--silent]");
            return 1;
        }

        var localRepoPath = args[1];
        var docfxPath = args[2];
        bool silent = args.Contains("--silent");

        if (!Directory.Exists(localRepoPath))
        {
            Console.Error.WriteLine($"Directory not found: {localRepoPath}");
            return 1;
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

        return 0;
    }

    private static int HandleUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DocFX Git Cloning Utility - Command Reference");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine();
        Console.WriteLine("  clone <repo-url> <docfx-path> [options]");
        Console.WriteLine("    Clone a repository and parse DocFX project with sparse checkout");
        Console.WriteLine();
        Console.WriteLine("    Options:");
        Console.WriteLine("      --output <path>   Output directory (default: ./<repo-name>)");
        Console.WriteLine("      --branch <branch> Branch to clone (default: remote HEAD)");
        Console.WriteLine("      --silent          Suppress progress output");
        Console.WriteLine();
        Console.WriteLine("  parse <local-repo-path> <docfx-path> [options]");
        Console.WriteLine("    Parse an existing repository with on-demand file checkout");
        Console.WriteLine();
        Console.WriteLine("    Options:");
        Console.WriteLine("      --silent          Suppress progress output");
    }
}
