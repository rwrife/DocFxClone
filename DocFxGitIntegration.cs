using DocFxParser;
using Newtonsoft.Json;

namespace DocFxClone;

/// <summary>
/// Integrates GitCloningUtility with DocfxDependencyParser to intelligently
/// checkout only the files needed by a DocFX project.
/// </summary>
public class DocFxGitIntegration
{
    private readonly GitCloningUtility _gitUtility;
    private readonly IGitOperationCallback _gitCallback;

    public DocFxGitIntegration(GitCloningUtility gitUtility, IGitOperationCallback? gitCallback = null)
    {
        _gitUtility = gitUtility ?? throw new ArgumentNullException(nameof(gitUtility));
        _gitCallback = gitCallback ?? new SilentGitOperationCallback();
    }

    /// <summary>
    /// Initialize a sparse clone and parse the DocFX project, checking out files on demand.
    /// </summary>
    /// <param name="repositoryUrl">The git repository URL</param>
    /// <param name="docfxJsonPath">The path to docfx.json within the repository (or directory containing docfx.json)</param>
    /// <param name="branch">Optional branch name</param>
    /// <param name="createDefault">Create a default docfx.json if one is not found</param>
    /// <returns>The DocFX dependency result</returns>
    public async Task<DocfxDependencyResult> CloneAndParseAsync(
        string repositoryUrl, 
        string docfxJsonPath, 
        string? branch = null,
        bool createDefault = false)
    {
        // Normalize the docfx path
        docfxJsonPath = NormalizeDocfxPath(docfxJsonPath);

        // Initialize sparse checkout
        await _gitUtility.InitializeSparseCloneAsync(repositoryUrl, branch);

        // Try to checkout the docfx.json file
        var fullDocfxPath = _gitUtility.GetFullPath(docfxJsonPath);
        bool docfxExists = await TryCheckoutDocfxJsonAsync(docfxJsonPath, fullDocfxPath, createDefault);

        if (!docfxExists)
        {
            throw new FileNotFoundException($"DocFX configuration file not found: {docfxJsonPath}");
        }

        // Pre-checkout files that match docfx.json glob patterns so the parser can enumerate them
        await PreCheckoutMatchingFilesAsync(fullDocfxPath);

        // Create a custom file access callback that checks out files on demand
        var fileCallback = new GitFileAccessCallback(_gitUtility);

        // Parse the DocFX project - the callback will check out files as needed
        var parser = new DocfxDependencyParser(fileCallback);
        var result = parser.Collect(fullDocfxPath);

        // After parsing, ensure all files in the result are checked out
        await CheckoutAllReferencedFilesAsync(result, fullDocfxPath);

        return result;
    }

    /// <summary>
    /// Parse an already cloned DocFX project with on-demand file checkout.
    /// Assumes the repository has been initialized with InitializeSparseCloneAsync.
    /// </summary>
    /// <param name="docfxJsonPath">The path to docfx.json within the repository (or directory containing docfx.json)</param>
    /// <param name="createDefault">Create a default docfx.json if one is not found</param>
    /// <returns>The DocFX dependency result</returns>
    public async Task<DocfxDependencyResult> ParseWithCheckoutAsync(string docfxJsonPath, bool createDefault = false)
    {
        // Normalize the docfx path
        docfxJsonPath = NormalizeDocfxPath(docfxJsonPath);

        // Check if this is a regular git repository (not sparse clone)
        var fullDocfxPath = _gitUtility.GetFullPath(docfxJsonPath);
        bool isRegularGitRepo = IsRegularGitRepository();
        
        if (isRegularGitRepo)
        {
            // For regular git repositories, just check if docfx.json exists or create it
            if (!File.Exists(fullDocfxPath))
            {
                if (createDefault)
                {
                    await CreateDefaultDocfxJsonAsync(fullDocfxPath);
                    _gitCallback.OnGitSuccess("Created default docfx.json", $"Created default configuration at {docfxJsonPath}");
                }
                else
                {
                    throw new FileNotFoundException($"DocFX configuration file not found: {docfxJsonPath}");
                }
            }
            
            // For regular repositories, we don't need special file checkout - just parse directly
            var fileCallback = new RegularFileAccessCallback();
            var parser = new DocfxDependencyParser(fileCallback);
            return parser.Collect(fullDocfxPath);
        }
        else
        {
            // Original sparse checkout logic
            bool docfxExists = await TryCheckoutDocfxJsonAsync(docfxJsonPath, fullDocfxPath, createDefault);

            if (!docfxExists)
            {
                throw new FileNotFoundException($"DocFX configuration file not found: {docfxJsonPath}");
            }

            // Pre-checkout files that match docfx.json glob patterns so the parser can enumerate them
            await PreCheckoutMatchingFilesAsync(fullDocfxPath);

            // Create a custom file access callback that checks out files on demand
            var fileCallback = new GitFileAccessCallback(_gitUtility);

            // Parse the DocFX project - the callback will check out files as needed
            var parser = new DocfxDependencyParser(fileCallback);
            var result = parser.Collect(fullDocfxPath);

            // After parsing, ensure all files in the result are checked out
            await CheckoutAllReferencedFilesAsync(result, fullDocfxPath);

            return result;
        }
    }

    /// <summary>
    /// Try to checkout the docfx.json file, creating a default one if it doesn't exist and createDefault is true.
    /// </summary>
    /// <param name="docfxJsonPath">The relative path to docfx.json within the repository</param>
    /// <param name="fullDocfxPath">The full local path to docfx.json</param>
    /// <param name="createDefault">Whether to create a default docfx.json if not found</param>
    /// <returns>True if the file exists or was created, false otherwise</returns>
    private async Task<bool> TryCheckoutDocfxJsonAsync(string docfxJsonPath, string fullDocfxPath, bool createDefault)
    {
        try
        {
            // Try to checkout the file first
            await _gitUtility.CheckoutFileAsync(docfxJsonPath);
            bool exists = File.Exists(fullDocfxPath);
            
            // If the file exists after checkout, we're done
            if (exists)
            {
                return true;
            }
            
            // If the file doesn't exist after checkout and createDefault is true, create a default one
            if (createDefault)
            {
                try
                {
                    await CreateDefaultDocfxJsonAsync(fullDocfxPath);
                    
                    // Verify the file was created successfully
                    if (File.Exists(fullDocfxPath))
                    {
                        _gitCallback.OnGitSuccess("Created default docfx.json", $"Created default configuration at {docfxJsonPath}");
                        return true;
                    }
                    else
                    {
                        _gitCallback.OnGitError("Create default docfx.json", $"Failed to create file at {fullDocfxPath}");
                        return false;
                    }
                }
                catch (Exception createEx)
                {
                    _gitCallback.OnGitError("Create default docfx.json", $"Failed to create default file: {createEx.Message}");
                    return false;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            // If checkout fails and createDefault is true, create a default docfx.json
            if (createDefault)
            {
                try
                {
                    await CreateDefaultDocfxJsonAsync(fullDocfxPath);
                    
                    // Verify the file was created successfully
                    if (File.Exists(fullDocfxPath))
                    {
                        _gitCallback.OnGitSuccess("Created default docfx.json", $"Created default configuration at {docfxJsonPath}");
                        return true;
                    }
                    else
                    {
                        _gitCallback.OnGitError("Create default docfx.json", $"Failed to create file at {fullDocfxPath}");
                        return false;
                    }
                }
                catch (Exception createEx)
                {
                    _gitCallback.OnGitError("Create default docfx.json", $"Failed to create default file: {createEx.Message}");
                    return false;
                }
            }
            else
            {
                _gitCallback.OnGitError("Checkout docfx.json", $"File not found and createDefault is false: {ex.Message}");
            }
            return false;
        }
    }

    /// <summary>
    /// Creates a default docfx.json file with content and resource sections.
    /// </summary>
    /// <param name="fullDocfxPath">The full local path where to create the docfx.json file</param>
    private async Task CreateDefaultDocfxJsonAsync(string fullDocfxPath)
    {
        var defaultConfig = new
        {
            build = new
            {
                content = new[]
                {
                    new
                    {
                        files = new[] { "**/*.yml", "**/*.md" }
                    }
                },
                resource = new[]
                {
                    new
                    {
                        files = new[] { "**/*.jpg", "**/*.png" }
                    }
                },
                dest = "_site"
            }
        };

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(fullDocfxPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonConvert.SerializeObject(defaultConfig, Formatting.Indented);
        await File.WriteAllTextAsync(fullDocfxPath, json);
    }

    /// <summary>
    /// Check if we're working with a regular git repository (not a sparse clone setup).
    /// </summary>
    private bool IsRegularGitRepository()
    {
        var gitDir = Path.Combine(_gitUtility.GetFullPath(""), ".git");
        return Directory.Exists(gitDir);
    }

    /// <summary>
    /// Normalize the docfx path by appending "docfx.json" if it doesn't already end with it.
    /// </summary>
    /// <param name="docfxPath">The provided docfx path</param>
    /// <returns>The normalized path ending with docfx.json</returns>
    private string NormalizeDocfxPath(string docfxPath)
    {
        if (string.IsNullOrWhiteSpace(docfxPath))
        {
            return "docfx.json";
        }

        // If the path already ends with docfx.json (case insensitive), return as-is
        if (docfxPath.EndsWith("docfx.json", StringComparison.OrdinalIgnoreCase))
        {
            return docfxPath;
        }

        // If it ends with a directory separator, just append docfx.json
        if (docfxPath.EndsWith("/") || docfxPath.EndsWith("\\"))
        {
            return docfxPath + "docfx.json";
        }

        // Otherwise, assume it's a directory and append /docfx.json
        return docfxPath + "/docfx.json";
    }

    /// <summary>
    /// Pre-checkout files from git's tree that match the glob patterns defined in docfx.json.
    /// This allows the DocFxParser to enumerate files using FileSystemGlobbing even in a sparse-checkout repo.
    /// </summary>
    private async Task PreCheckoutMatchingFilesAsync(string fullDocfxPath)
    {
        var docfxDirectory = Path.GetDirectoryName(fullDocfxPath)!;
        var repoRoot = _gitUtility.GetFullPath("");
        
        // Get all files from git's tree
        var allFilesInTree = await _gitUtility.ListFilesInTreeAsync();
        
        // Parse docfx.json to get glob patterns
        var docfxJson = await File.ReadAllTextAsync(fullDocfxPath);
        var docfxConfig = Newtonsoft.Json.Linq.JObject.Parse(docfxJson);
        
        var filesToCheckout = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Process content sections
        var contentSections = docfxConfig["build"]?["content"] as Newtonsoft.Json.Linq.JArray;
        if (contentSections != null)
        {
            foreach (var section in contentSections)
            {
                await ProcessSectionAsync(section, allFilesInTree, docfxDirectory, repoRoot, filesToCheckout);
            }
        }
        
        // Process resource sections
        var resourceSections = docfxConfig["build"]?["resource"] as Newtonsoft.Json.Linq.JArray;
        if (resourceSections != null)
        {
            foreach (var section in resourceSections)
            {
                await ProcessSectionAsync(section, allFilesInTree, docfxDirectory, repoRoot, filesToCheckout);
            }
        }
        
        if (filesToCheckout.Count > 0)
        {
            _gitCallback.OnGitSuccess("Pre-checkout matched files", $"Checking out {filesToCheckout.Count} files matching docfx.json patterns");
            await _gitUtility.CheckoutFilesAsync(filesToCheckout);
        }
    }

    private async Task ProcessSectionAsync(
        Newtonsoft.Json.Linq.JToken section,
        List<string> allFilesInTree,
        string docfxDirectory,
        string repoRoot,
        HashSet<string> filesToCheckout)
    {
        var srcPath = section["src"]?.ToString() ?? "";
        var srcDirectory = string.IsNullOrWhiteSpace(srcPath)
            ? docfxDirectory
            : Path.GetFullPath(Path.Combine(docfxDirectory, srcPath));
        
        var srcRelativePath = Path.GetRelativePath(repoRoot, srcDirectory).Replace('\\', '/');
        
        // Ensure srcRelativePath doesn't have trailing slash for consistent matching
        srcRelativePath = srcRelativePath.TrimEnd('/');
        
        // Get file patterns
        var filesToken = section["files"];
        if (filesToken == null)
            return;
            
        var patterns = new List<string>();
        if (filesToken is Newtonsoft.Json.Linq.JArray filesArray)
        {
            patterns.AddRange(filesArray.Select(f => f.ToString()));
        }
        else
        {
            patterns.Add(filesToken.ToString());
        }
        
        // Get exclude patterns
        var excludePatterns = new List<string>();
        var excludeToken = section["exclude"];
        if (excludeToken is Newtonsoft.Json.Linq.JArray excludeArray)
        {
            excludePatterns.AddRange(excludeArray.Select(e => e.ToString()));
        }
        
        // Use Microsoft.Extensions.FileSystemGlobbing to match patterns
        var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            matcher.AddInclude(pattern);
        }
        foreach (var exclude in excludePatterns)
        {
            matcher.AddExclude(exclude);
        }
        
        // Match files from git's tree against the patterns
        // Since Matcher.Execute requires a DirectoryInfo, we'll do simple matching ourselves
        foreach (var file in allFilesInTree)
        {
            if (file.StartsWith(srcRelativePath + "/", StringComparison.OrdinalIgnoreCase) ||
                file.Equals(srcRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                var relativeToSrc = file.StartsWith(srcRelativePath + "/", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(srcRelativePath.Length + 1)
                    : "";
                
                if (string.IsNullOrEmpty(relativeToSrc))
                    continue;
                
                // Simple glob matching - check if file matches any include pattern
                bool matches = false;
                foreach (var pattern in patterns)
                {
                    if (MatchesGlob(relativeToSrc, pattern))
                    {
                        matches = true;
                        break;
                    }
                }
                
                // Check exclude patterns
                if (matches)
                {
                    foreach (var exclude in excludePatterns)
                    {
                        if (MatchesGlob(relativeToSrc, exclude))
                        {
                            matches = false;
                            break;
                        }
                    }
                }
                
                if (matches)
                {
                    filesToCheckout.Add(file);
                }
            }
        }
        
        await Task.CompletedTask;
    }

    private bool MatchesGlob(string path, string pattern)
    {
        // Simple glob matching for common patterns
        if (pattern == "**/*" || pattern == "**")
            return true;
        
        // Handle brace expansion: **/*.{md,yml} -> **/*.md OR **/*.yml
        if (pattern.Contains('{') && pattern.Contains('}'))
        {
            var braceStart = pattern.IndexOf('{');
            var braceEnd = pattern.IndexOf('}');
            var prefix = pattern.Substring(0, braceStart);
            var suffix = pattern.Substring(braceEnd + 1);
            var options = pattern.Substring(braceStart + 1, braceEnd - braceStart - 1).Split(',');
            
            foreach (var option in options)
            {
                var expandedPattern = prefix + option.Trim() + suffix;
                if (MatchesGlob(path, expandedPattern))
                    return true;
            }
            return false;
        }
            
        // Convert glob pattern to regex
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*\\*/\\*", "(.*?/)?[^/]*")  // **/* should match files in root or subdirs
            .Replace("\\*\\*/", "(.*?/)?")          // **/ should match nothing or path/
            .Replace("\\*\\*", ".*")                // ** should match anything
            .Replace("\\*", "[^/]*")                // * should match anything except /
            .Replace("\\?", ".")                    // ? should match any single character
            + "$";
            
        return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Checkout all files referenced in the DocFX dependency result.
    /// This ensures that all files discovered by the parser (both during callback and in final result) are materialized.
    /// </summary>
    private async Task CheckoutAllReferencedFilesAsync(DocfxDependencyResult result, string fullDocfxPath)
    {
        var filesToCheckout = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docfxDirectory = Path.GetDirectoryName(fullDocfxPath)!;
        var repoRoot = _gitUtility.GetFullPath("");
        
        foreach (var file in result.Files)
        {
            // Check out the file itself
            var fullPath = Path.GetFullPath(Path.Combine(docfxDirectory, file.Path));
            var relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
            
            if (!_gitUtility.IsFileCheckedOut(relativePath))
            {
                filesToCheckout.Add(relativePath);
            }
            
            // Also check out all referenced files
            if (file.References != null)
            {
                foreach (var reference in file.References)
                {
                    if (string.IsNullOrWhiteSpace(reference))
                        continue;
                        
                    var refFullPath = Path.GetFullPath(Path.Combine(docfxDirectory, reference));
                    var refRelativePath = Path.GetRelativePath(repoRoot, refFullPath).Replace('\\', '/');
                    
                    if (!_gitUtility.IsFileCheckedOut(refRelativePath))
                    {
                        filesToCheckout.Add(refRelativePath);
                    }
                }
            }
        }

        if (filesToCheckout.Count > 0)
        {
            _gitCallback.OnGitSuccess("Checking out remaining files", $"{filesToCheckout.Count} files not yet checked out");
            await _gitUtility.CheckoutFilesAsync(filesToCheckout);
        }
    }
}

/// <summary>
/// File access callback that checks out files from git on demand.
/// </summary>
internal class GitFileAccessCallback : IFileAccessCallback
{
    private readonly GitCloningUtility _gitUtility;

    public GitFileAccessCallback(GitCloningUtility gitUtility)
    {
        _gitUtility = gitUtility ?? throw new ArgumentNullException(nameof(gitUtility));
    }

    public bool OnBeforeFileRead(string filePath, string purpose)
    {
        try
        {
            // Convert absolute path to relative path within the repo
            var repoRoot = _gitUtility.GetFullPath("");
            var relativePath = Path.GetRelativePath(repoRoot, filePath);

            // If the file doesn't exist locally, try to check it out
            if (!File.Exists(filePath) && !_gitUtility.IsFileCheckedOut(relativePath))
            {
                // Use synchronous wait - this is a callback from sync code
                _gitUtility.CheckoutFileAsync(relativePath).GetAwaiter().GetResult();
            }

            return File.Exists(filePath);
        }
        catch
        {
            // If checkout fails, return false to skip the file
            return false;
        }
    }

    public void OnAfterFileRead(string filePath, string purpose)
    {
        // Nothing to do after reading
    }

    public void OnFileReadError(string filePath, string purpose, Exception exception)
    {
        // Errors are handled by the parser
    }
}

/// <summary>
/// File access callback for regular git repositories (non-sparse checkout).
/// </summary>
internal class RegularFileAccessCallback : IFileAccessCallback
{
    public bool OnBeforeFileRead(string filePath, string purpose)
    {
        // For regular repositories, files should already exist
        return File.Exists(filePath);
    }

    public void OnAfterFileRead(string filePath, string purpose)
    {
        // Nothing to do after reading
    }

    public void OnFileReadError(string filePath, string purpose, Exception exception)
    {
        // Errors are handled by the parser
    }
}
