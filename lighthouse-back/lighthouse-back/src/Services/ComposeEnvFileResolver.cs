using System.Text;
using Lighthouse.Data;
using Lighthouse.Models;
using Microsoft.EntityFrameworkCore;

namespace Lighthouse.Services;

/// <summary>
/// Resolves docker compose <c>--env-file</c> arguments.
/// <para>
/// Docker does not persist the <c>--env-file</c> originally used to start a project, so when the
/// manager recreates containers (e.g. during an image update) a custom global env file is lost and
/// interpolation variables (network names, etc.) resolve to blank. This resolver re-applies a
/// configurable global env file (the <c>ComposeGlobalEnvFileKey</c> setting) on every
/// manager-issued <c>up</c>/<c>pull</c>, combined with the project-adjacent <c>.env</c>.
/// </para>
/// </summary>
public interface IComposeEnvFileResolver
{
    /// <summary>
    /// Builds the top-level <c>--env-file</c> flags to insert before a compose subcommand,
    /// e.g. <c>--env-file "global" --env-file "dir/.env" </c>. Returns an empty string when no
    /// env files apply; a trailing space is included when the result is non-empty so it can be
    /// concatenated directly before the subcommand.
    /// </summary>
    Task<string> BuildEnvFileArgsAsync(string? composeDirectory, CancellationToken ct = default);

    /// <summary>
    /// Returns the env files docker compose uses for variable interpolation of a project, lowest
    /// precedence first: the configured global env file (when set and present), then the
    /// project-adjacent <c>.env</c> (when present).
    /// </summary>
    Task<IReadOnlyList<string>> GetEnvFilePathsAsync(string? composeDirectory, CancellationToken ct = default);
}

public class ComposeEnvFileResolver : IComposeEnvFileResolver
{
    /// <summary>AppSettings key holding the path to a global .env file (empty/absent disables it).</summary>
    public const string ComposeGlobalEnvFileKey = "ComposeGlobalEnvFile";

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ComposeEnvFileResolver> _logger;

    public ComposeEnvFileResolver(IServiceProvider serviceProvider, ILogger<ComposeEnvFileResolver> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<string> BuildEnvFileArgsAsync(string? composeDirectory, CancellationToken ct = default)
    {
        string? globalEnvFile = await ReadGlobalEnvFileAsync(ct);

        // No usable global file: rely on docker's default .env auto-discovery from the working
        // directory (the caller sets the compose file's directory as the working directory).
        if (string.IsNullOrWhiteSpace(globalEnvFile))
        {
            return string.Empty;
        }

        var args = new StringBuilder();
        args.Append($"--env-file \"{globalEnvFile}\" ");

        // Once any --env-file is passed, compose stops auto-loading the default .env. Re-add the
        // project-adjacent .env explicitly, last, so project-local values override the global ones.
        if (!string.IsNullOrEmpty(composeDirectory))
        {
            string adjacentEnv = Path.Combine(composeDirectory, ".env");
            if (File.Exists(adjacentEnv))
            {
                args.Append($"--env-file \"{adjacentEnv}\" ");
            }
        }

        return args.ToString();
    }

    public async Task<IReadOnlyList<string>> GetEnvFilePathsAsync(string? composeDirectory, CancellationToken ct = default)
    {
        var paths = new List<string>();

        string? globalEnvFile = await ReadGlobalEnvFileAsync(ct);
        if (!string.IsNullOrWhiteSpace(globalEnvFile))
        {
            paths.Add(globalEnvFile);
        }

        if (!string.IsNullOrEmpty(composeDirectory))
        {
            string adjacentEnv = Path.Combine(composeDirectory, ".env");
            if (File.Exists(adjacentEnv))
            {
                paths.Add(adjacentEnv);
            }
        }

        return paths;
    }

    private async Task<string?> ReadGlobalEnvFileAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            AppSetting? setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == ComposeGlobalEnvFileKey, ct);

            string? value = setting?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // Skip (rather than pass) a missing file: docker compose errors out on a non-existent
            // --env-file, which would break the operation. Treat misconfiguration as "off".
            if (!File.Exists(value))
            {
                _logger.LogWarning(
                    "Configured global env file '{Path}' does not exist; ignoring it for this operation", value);
                return null;
            }

            return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Key} setting", ComposeGlobalEnvFileKey);
            return null;
        }
    }
}
