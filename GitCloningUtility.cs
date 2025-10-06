using System.Diagnostics;
using System.Text;

namespace DocFxClone;

/// <summary>
/// Callback interface for git operations.
/// Implement this to be notified when files are checked out.
/// </summary>
public interface IGitOperationCallback
{
    /// <summary>
    /// Called when a file is about to be checked out from the repository.
    /// </summary>
    /// <param name="relativePath">The relative path of the file within the repository</param>
    void OnFileCheckout(string relativePath);

    /// <summary>
    /// Called when a git operation encounters an error.
    /// </summary>
    /// <param name="operation">The operation that failed</param>
    /// <param name="error">The error message</param>
    void OnGitError(string operation, string error);

    /// <summary>
    /// Called when a git operation completes successfully.
    /// </summary>
    /// <param name="operation">The operation that completed</param>
    /// <param name="details">Optional details about the operation</param>
    void OnGitSuccess(string operation, string? details = null);
}

/// <summary>
/// Default implementation that writes to console.
/// </summary>
public class ConsoleGitOperationCallback : IGitOperationCallback
{
    public void OnFileCheckout(string relativePath)
    {
        Console.WriteLine($"Checking out: {relativePath}");
    }

    public void OnGitError(string operation, string error)
    {
        Console.Error.WriteLine($"Git error during {operation}: {error}");
    }

    public void OnGitSuccess(string operation, string? details = null)
    {
        var message = string.IsNullOrEmpty(details) 
            ? $"Git operation succeeded: {operation}" 
            : $"Git operation succeeded: {operation} - {details}";
        Console.WriteLine(message);
    }
}

/// <summary>
/// Silent implementation that does nothing.
/// </summary>
public class SilentGitOperationCallback : IGitOperationCallback
{
    public void OnFileCheckout(string relativePath) { }
    public void OnGitError(string operation, string error) { }
    public void OnGitSuccess(string operation, string? details = null) { }
}

/// <summary>
/// Intelligent git cloning utility that performs sparse checkouts for DocFX projects.
/// Only downloads the files that are actually needed.
/// </summary>
public class GitCloningUtility
{
    private readonly IGitOperationCallback _callback;
    private readonly string _localPath;
    private readonly HashSet<string> _checkedOutFiles;
    private bool _isInitialized;

    public GitCloningUtility(string localPath) 
        : this(localPath, new ConsoleGitOperationCallback())
    {
    }

    public GitCloningUtility(string localPath, IGitOperationCallback callback)
    {
        _localPath = Path.GetFullPath(localPath);
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _checkedOutFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _isInitialized = false;
    }

    /// <summary>
    /// Initialize a sparse checkout of the repository.
    /// This creates an empty clone with only folder/file names, no content.
    /// </summary>
    /// <param name="repositoryUrl">The git repository URL</param>
    /// <param name="branch">Optional branch name (defaults to remote HEAD)</param>
    public async Task InitializeSparseCloneAsync(string repositoryUrl, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("Repository URL cannot be null or empty", nameof(repositoryUrl));
        }

        if (_isInitialized)
        {
            throw new InvalidOperationException("Repository already initialized");
        }

        // Create the directory if it doesn't exist
        Directory.CreateDirectory(_localPath);

        try
        {
            // Check if .git directory exists
            var gitDir = Path.Combine(_localPath, ".git");
            bool isExistingRepo = Directory.Exists(gitDir);

            if (!isExistingRepo)
            {
                // Initialize git repository
                await RunGitCommandAsync("init", "Initializing repository");
            }
            else
            {
                _callback.OnGitSuccess("Repository", "Using existing git repository");
            }

            // Check if remote origin exists
            bool hasOrigin = false;
            try
            {
                var remoteUrl = await RunGitCommandAsync("remote get-url origin", "Checking remote", suppressOutput: true);
                hasOrigin = !string.IsNullOrWhiteSpace(remoteUrl);
                
                if (hasOrigin)
                {
                    // Update the remote URL if it's different
                    if (!remoteUrl.Trim().Equals(repositoryUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        await RunGitCommandAsync($"remote set-url origin \"{repositoryUrl}\"", "Updating remote URL");
                    }
                    else
                    {
                        _callback.OnGitSuccess("Remote", "Using existing remote origin");
                    }
                }
            }
            catch
            {
                // Remote doesn't exist, we'll add it
                hasOrigin = false;
            }

            // Add remote if it doesn't exist
            if (!hasOrigin)
            {
                await RunGitCommandAsync($"remote add origin \"{repositoryUrl}\"", "Adding remote");
            }

            // Use --filter=blob:none to get tree and commit objects but not blob content
            // This gives us the file/folder structure without downloading file contents
            var fetchCommand = string.IsNullOrEmpty(branch) 
                ? "fetch --depth=1 --filter=blob:none origin" 
                : $"fetch --depth=1 --filter=blob:none origin {branch}";
            
            await RunGitCommandAsync(fetchCommand, "Fetching repository structure (no file content)");

            // Configure sparse-checkout BEFORE checking out to prevent materializing all files
            await RunGitCommandAsync("config core.sparseCheckout true", "Enabling sparse-checkout");
            await RunGitCommandAsync("config core.sparseCheckoutCone false", "Configuring sparse-checkout mode");
            
            // Create sparse-checkout file with nothing in it (no files will be materialized)
            var sparseCheckoutPath = Path.Combine(_localPath, ".git", "info", "sparse-checkout");
            Directory.CreateDirectory(Path.GetDirectoryName(sparseCheckoutPath)!);
            // Start with empty - we'll add files as needed
            await File.WriteAllTextAsync(sparseCheckoutPath, "");

            // Determine the branch name to use
            string branchName = string.IsNullOrEmpty(branch) ? "main" : branch;
            
            // Check if the branch already exists locally
            bool branchExists = false;
            try
            {
                await RunGitCommandAsync($"rev-parse --verify {branchName}", "Checking branch", suppressOutput: true);
                branchExists = true;
            }
            catch
            {
                branchExists = false;
            }

            if (branchExists)
            {
                // Branch exists, checkout and reset
                await RunGitCommandAsync($"checkout {branchName}", "Switching to branch");
                await RunGitCommandAsync("reset --hard FETCH_HEAD", "Resetting to remote");
            }
            else
            {
                // Create new branch - with sparse-checkout enabled and empty list, no files will be materialized
                var checkoutCommand = $"checkout -b {branchName} FETCH_HEAD";
                await RunGitCommandAsync(checkoutCommand, "Setting up tracking branch");
            }

            // At this point:
            // - Repository is cloned with partial clone (no blob content downloaded)
            // - Sparse-checkout is enabled with empty list (no files in working directory)
            // - File tree structure is in git (can query with ls-tree, etc)
            // - Files will be checked out on-demand
            
            _callback.OnGitSuccess("Repository structure", "Repository initialized, files will be checked out on demand");

            _isInitialized = true;
            _callback.OnGitSuccess("InitializeSparseClone", $"Repository initialized at {_localPath}");
        }
        catch (Exception ex)
        {
            _callback.OnGitError("InitializeSparseClone", ex.Message);
            throw new InvalidOperationException($"Failed to initialize sparse clone: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Check out a specific file from the repository.
    /// With partial clone + sparse-checkout, this adds the file to sparse-checkout list and fetches its content.
    /// </summary>
    /// <param name="relativePath">The relative path within the repository</param>
    public async Task CheckoutFileAsync(string relativePath)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Repository not initialized. Call InitializeSparseCloneAsync first.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path cannot be null or empty", nameof(relativePath));
        }

        var normalizedPath = NormalizePath(relativePath);

        // Skip if already checked out
        if (_checkedOutFiles.Contains(normalizedPath))
        {
            return;
        }

        _callback.OnFileCheckout(normalizedPath);

        try
        {
            // Use sparse-checkout add to both add to list AND fetch blob content
            await RunGitCommandAsync($"sparse-checkout add \"{normalizedPath}\"", $"Fetching and checking out {normalizedPath}", suppressOutput: true);

            _checkedOutFiles.Add(normalizedPath);
        }
        catch (Exception ex)
        {
            _callback.OnGitError($"CheckoutFile: {normalizedPath}", ex.Message);
            throw new InvalidOperationException($"Failed to checkout file '{relativePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Check out multiple files from the repository.
    /// With partial clone + sparse-checkout, this adds files to sparse-checkout list and fetches their content.
    /// </summary>
    /// <param name="relativePaths">The relative paths within the repository</param>
    public async Task CheckoutFilesAsync(IEnumerable<string> relativePaths)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Repository not initialized. Call InitializeSparseCloneAsync first.");
        }

        var pathsToCheckout = relativePaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Where(p => !_checkedOutFiles.Contains(p))
            .ToList();

        if (pathsToCheckout.Count == 0)
        {
            return;
        }

        try
        {
            // Notify about each file
            foreach (var path in pathsToCheckout)
            {
                _callback.OnFileCheckout(path);
            }

            // Use sparse-checkout add to add all files at once and fetch their blob content
            var pathArgs = string.Join(" ", pathsToCheckout.Select(p => $"\"{p}\""));
            await RunGitCommandAsync($"sparse-checkout add {pathArgs}", "Fetching and checking out files", suppressOutput: true);

            foreach (var path in pathsToCheckout)
            {
                _checkedOutFiles.Add(path);
            }
        }
        catch (Exception ex)
        {
            _callback.OnGitError("CheckoutFiles", ex.Message);
            throw new InvalidOperationException($"Failed to checkout files: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get the full local path for a relative path within the repository.
    /// </summary>
    /// <param name="relativePath">The relative path within the repository</param>
    /// <returns>The full local path</returns>
    public string GetFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return _localPath;
        }

        var normalized = NormalizePath(relativePath);
        return Path.Combine(_localPath, normalized);
    }

    /// <summary>
    /// Check if a file has been checked out.
    /// </summary>
    /// <param name="relativePath">The relative path within the repository</param>
    /// <returns>True if the file has been checked out</returns>
    public bool IsFileCheckedOut(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return _checkedOutFiles.Contains(normalized);
    }

    /// <summary>
    /// Get all checked out files.
    /// </summary>
    /// <returns>Collection of relative paths that have been checked out</returns>
    public IReadOnlyCollection<string> GetCheckedOutFiles()
    {
        return _checkedOutFiles.ToList().AsReadOnly();
    }

    /// <summary>
    /// List all files in the git tree (from git database, not working directory).
    /// This allows enumerating files without materializing them.
    /// </summary>
    /// <param name="path">Optional path to list files from (relative to repo root)</param>
    /// <returns>List of file paths relative to repository root</returns>
    public async Task<List<string>> ListFilesInTreeAsync(string? path = null)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Repository not initialized. Call InitializeSparseCloneAsync first.");
        }

        try
        {
            // Use git ls-tree to list files from git's database
            var pathArg = string.IsNullOrWhiteSpace(path) ? "" : $" {path}";
            var output = await RunGitCommandAsync($"ls-tree -r --name-only HEAD{pathArg}", "Listing files in git tree", suppressOutput: true);
            
            var files = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrEmpty(f))
                .ToList();
            
            return files;
        }
        catch (Exception ex)
        {
            _callback.OnGitError("ListFilesInTree", ex.Message);
            throw new InvalidOperationException($"Failed to list files in tree: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Run a git command in the repository directory.
    /// </summary>
    private async Task<string> RunGitCommandAsync(string arguments, string operationName, bool suppressOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _localPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (process.ExitCode != 0)
        {
            var errorMessage = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"Git command failed: {errorMessage}");
        }

        if (!suppressOutput && !string.IsNullOrWhiteSpace(output))
        {
            _callback.OnGitSuccess(operationName, output.Trim());
        }

        return output;
    }

    /// <summary>
    /// Normalize a path to use forward slashes (git convention).
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }
}
