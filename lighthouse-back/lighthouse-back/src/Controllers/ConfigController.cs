using Cronos;
using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using Lighthouse.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lighthouse.Controllers;

/// <summary>
/// Configuration management endpoints (Admin only)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class ConfigController : BaseController
{
    private readonly AppDbContext _context;
    private readonly ILogger<ConfigController> _logger;
    private readonly LogLevelService _logLevelService;
    private readonly IConfiguration _configuration;

    public ConfigController(AppDbContext context, ILogger<ConfigController> logger, LogLevelService logLevelService, IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _logLevelService = logLevelService;
        _configuration = configuration;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is one of the allowed roots or lives under one.
    /// Uses a trailing-separator comparison so a sibling sharing a root's name prefix is rejected.
    /// </summary>
    private static bool IsWithinAllowedRoots(string fullPath, string[] allowedRoots)
    {
        foreach (string root in allowedRoots)
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
                return true;

            string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    #region Directory Browser

    /// <summary>
    /// Browse filesystem directories (for folder picker)
    /// </summary>
    [HttpGet("browse")]
    [ProducesResponseType(typeof(ApiResponse<DirectoryBrowseResult>), StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<DirectoryBrowseResult>> BrowseDirectories(
        [FromQuery] string? path = null,
        [FromQuery] bool includeFiles = false)
    {
        try
        {
            // Optional allowlist: when Config:AllowedBrowsePaths is set, the filesystem
            // browser is restricted to those subtrees. Empty (default) keeps full access
            // for the admin folder/file picker.
            string[] allowedRoots = (_configuration.GetSection("Config:AllowedBrowsePaths").Get<string[]>() ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath)
                .ToArray();

            // Default to root directories if no path provided
            string currentPath = path ?? string.Empty;

            // Validate and normalize path
            if (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    currentPath = Path.GetFullPath(currentPath);
                    if (allowedRoots.Length > 0 && !IsWithinAllowedRoots(currentPath, allowedRoots))
                    {
                        _logger.LogWarning("Directory browse denied outside allowed roots: {Path}", currentPath);
                        return StatusCode(403, ApiResponse.Fail<DirectoryBrowseResult>("Access denied to this directory"));
                    }
                    if (!Directory.Exists(currentPath))
                    {
                        return BadRequest(ApiResponse.Fail<DirectoryBrowseResult>("Directory does not exist"));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Invalid path provided: {Path}", currentPath);
                    return BadRequest(ApiResponse.Fail<DirectoryBrowseResult>("Invalid path"));
                }
            }

            DirectoryBrowseResult result = new DirectoryBrowseResult
            {
                CurrentPath = currentPath,
                Directories = new List<DirectoryBrowseInfo>()
            };

            if (string.IsNullOrEmpty(currentPath))
            {
                // With an allowlist configured, the roots are the only entry points.
                if (allowedRoots.Length > 0)
                {
                    result.Directories = allowedRoots
                        .Where(Directory.Exists)
                        .Select(root => new DirectoryBrowseInfo { Name = root, Path = root, IsAccessible = true })
                        .ToList();
                    return Ok(ApiResponse.Ok(result, "Directory contents retrieved successfully"));
                }

                // Return root drives/directories
                if (OperatingSystem.IsWindows())
                {
                    List<DirectoryBrowseInfo> drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new DirectoryBrowseInfo
                        {
                            Name = d.Name,
                            Path = d.RootDirectory.FullName,
                            IsAccessible = true
                        })
                        .ToList();
                    result.Directories = drives;
                }
                else
                {
                    // Unix-like systems start from root
                    result.CurrentPath = "/";
                    currentPath = "/";
                }
            }

            if (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    DirectoryInfo dirInfo = new System.IO.DirectoryInfo(currentPath);

                    // Get parent directory info
                    if (dirInfo.Parent != null)
                    {
                        result.ParentPath = dirInfo.Parent.FullName;
                    }

                    // Get subdirectories
                    List<DirectoryBrowseInfo> directories = dirInfo.GetDirectories()
                        .OrderBy(d => d.Name)
                        .Select(d =>
                        {
                            bool isAccessible = true;
                            try
                            {
                                // Test if we can access the directory
                                _ = d.GetDirectories();
                            }
                            catch
                            {
                                isAccessible = false;
                            }

                            return new DirectoryBrowseInfo
                            {
                                Name = d.Name,
                                Path = d.FullName,
                                IsAccessible = isAccessible
                            };
                        })
                        .ToList();

                    result.Directories = directories;

                    // Optionally list files (used by the file picker, e.g. selecting a global .env file)
                    if (includeFiles)
                    {
                        result.Files = dirInfo.GetFiles()
                            .OrderBy(f => f.Name)
                            .Select(f => new DirectoryBrowseInfo
                            {
                                Name = f.Name,
                                Path = f.FullName,
                                IsAccessible = true
                            })
                            .ToList();
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return StatusCode(403, ApiResponse.Fail<DirectoryBrowseResult>("Access denied to this directory"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error browsing directory: {Path}", currentPath);
                    return StatusCode(500, ApiResponse.Fail<DirectoryBrowseResult>("Error reading directory"));
                }
            }

            return Ok(ApiResponse.Ok(result, "Directory contents retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in directory browser");
            return StatusCode(500, ApiResponse.Fail<DirectoryBrowseResult>("Failed to browse directories"));
        }
    }

    #endregion

    #region Application Settings

    /// <summary>
    /// Get all application settings
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(ApiResponse<Dictionary<string, string>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<Dictionary<string, string>>>> GetSettings()
    {
        try
        {
            var settings = await _context.AppSettings.ToListAsync();
            Dictionary<string, string> settingsDict = settings.ToDictionary(
                s => s.Key,
                s => s.Key == DiscordNotificationService.WebhookUrlKey
                    ? (DiscordWebhookUrl.Mask(s.Value) ?? s.Value)
                    : s.Key == WebhookNotificationService.HeadersKey
                        ? WebhookHeaders.Mask(s.Value)
                        : s.Value);

            return Ok(ApiResponse.Ok(settingsDict, "Settings retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving settings");
            return StatusCode(500, ApiResponse.Fail<Dictionary<string, string>>("Failed to retrieve settings"));
        }
    }

    /// <summary>
    /// Update application setting
    /// </summary>
    [HttpPut("settings/{key}")]
    [ProducesResponseType(typeof(ApiResponse<AppSetting>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AppSetting>>> UpdateSetting(string key, [FromBody] UpdateSettingRequest request)
    {
        // Validate Auto-Update related settings
        var validationError = ValidateAutoUpdateSetting(key, request.Value);
        if (validationError != null)
        {
            return BadRequest(ApiResponse.Fail<AppSetting>(validationError));
        }

        try
        {
            var setting = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == key);

            if (setting == null)
            {
                // Create new setting
                setting = new AppSetting
                {
                    Key = key,
                    Value = request.Value,
                    Description = request.Description
                };
                _context.AppSettings.Add(setting);
            }
            else
            {
                // Update existing setting
                setting.Value = request.Value;
                if (request.Description != null)
                {
                    setting.Description = request.Description;
                }
            }

            await _context.SaveChangesAsync();

            if (key == WebhookNotificationService.HeadersKey)
            {
                // Header values are secrets: never log them nor echo them back.
                _logger.LogInformation("Setting updated: {Key}", key);
                var masked = new AppSetting
                {
                    Id = setting.Id,
                    Key = setting.Key,
                    Value = WebhookHeaders.Mask(setting.Value),
                    Description = setting.Description,
                    CreatedAt = setting.CreatedAt,
                    UpdatedAt = setting.UpdatedAt
                };
                return Ok(ApiResponse.Ok(masked, "Setting updated successfully"));
            }

            _logger.LogInformation("Setting updated: {Key} = {Value}", key, request.Value);

            return Ok(ApiResponse.Ok(setting, "Setting updated successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating setting {Key}", key);
            return StatusCode(500, ApiResponse.Fail<AppSetting>("Failed to update setting"));
        }
    }

    /// <summary>
    /// Delete application setting
    /// </summary>
    [HttpDelete("settings/{key}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteSetting(string key)
    {
        try
        {
            var setting = await _context.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (setting == null)
            {
                return NotFound(ApiResponse.Fail<object>($"Setting with key '{key}' not found"));
            }

            _context.AppSettings.Remove(setting);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Setting deleted: {Key}", key);

            return Ok(ApiResponse.Ok<object>(null, "Setting deleted successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting setting {Key}", key);
            return StatusCode(500, ApiResponse.Fail<object>("Failed to delete setting"));
        }
    }

    #endregion

    #region Log Level

    /// <summary>
    /// Get the current application log level and the available levels
    /// </summary>
    [HttpGet("log-level")]
    [ProducesResponseType(typeof(ApiResponse<LogLevelInfo>), StatusCodes.Status200OK)]
    public ActionResult<ApiResponse<LogLevelInfo>> GetLogLevel()
    {
        LogLevelInfo info = new LogLevelInfo(
            _logLevelService.GetCurrentLevel(),
            LogLevelService.GetAvailableLevels());

        return Ok(ApiResponse.Ok(info, "Log level retrieved successfully"));
    }

    /// <summary>
    /// Update the application log level. Takes effect immediately (no restart).
    /// </summary>
    [HttpPut("log-level")]
    [ProducesResponseType(typeof(ApiResponse<LogLevelInfo>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<LogLevelInfo>), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ApiResponse<LogLevelInfo>>> UpdateLogLevel([FromBody] UpdateLogLevelRequest request)
    {
        try
        {
            await _logLevelService.SetLevelAsync(request.Value);

            LogLevelInfo info = new LogLevelInfo(
                _logLevelService.GetCurrentLevel(),
                LogLevelService.GetAvailableLevels());

            return Ok(ApiResponse.Ok(info, "Log level updated successfully"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse.Fail<LogLevelInfo>(ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating log level");
            return StatusCode(500, ApiResponse.Fail<LogLevelInfo>("Failed to update log level"));
        }
    }

    #endregion

    /// <summary>
    /// Validates Auto-Update setting values. Returns error message if invalid, null if OK.
    /// </summary>
    private static string? ValidateAutoUpdateSetting(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (key == AutoUpdateComposeBackgroundService.EnabledKey
            || key == AutoUpdateAppBackgroundService.EnabledKey
            || key == AutoPruneImagesBackgroundService.EnabledKey
            || key == AutoPruneImagesBackgroundService.DanglingOnlyKey)
        {
            if (!bool.TryParse(value, out _))
            {
                return $"Value for '{key}' must be 'true' or 'false'";
            }
            return null;
        }

        if (key == AutoUpdateComposeBackgroundService.CronKey
            || key == AutoUpdateAppBackgroundService.CronKey
            || key == AutoPruneImagesBackgroundService.CronKey)
        {
            try
            {
                CronFormat format = value.Trim().Split(' ').Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
                CronExpression.Parse(value, format);
                return null;
            }
            catch (CronFormatException ex)
            {
                return $"Invalid cron expression for '{key}': {ex.Message}";
            }
        }

        if (key == DiscordNotificationService.EnabledKey)
        {
            if (!bool.TryParse(value, out _))
            {
                return $"Value for '{key}' must be 'true' or 'false'";
            }
            return null;
        }

        if (key == DiscordNotificationService.WebhookUrlKey)
        {
            // Empty is allowed (clears the webhook); otherwise must be a valid Discord webhook URL.
            if (!string.IsNullOrWhiteSpace(value) && !DiscordWebhookUrl.IsValid(value))
            {
                return "Invalid Discord webhook URL. Expected https://discord.com/api/webhooks/{id}/{token}";
            }
            return null;
        }

        if (key == WebhookNotificationService.EnabledKey)
        {
            if (!bool.TryParse(value, out _))
            {
                return $"Value for '{key}' must be 'true' or 'false'";
            }
            return null;
        }

        if (key == WebhookNotificationService.UrlKey)
        {
            // Empty is allowed (clears the URL).
            if (!string.IsNullOrWhiteSpace(value) && !WebhookUrl.IsValid(value))
            {
                return "Invalid webhook URL. Expected an absolute http(s) URL";
            }
            return null;
        }

        if (key == WebhookNotificationService.HeadersKey)
        {
            return WebhookHeaders.TryParse(value, out _, out string? headerError) ? null : headerError;
        }

        if (key == ComposeEnvFileResolver.ComposeGlobalEnvFileKey)
        {
            // Empty disables it. A non-empty path is accepted even when the file does not yet exist
            // (it may appear later); a missing file is logged and skipped at use time, not rejected here.
            return null;
        }

        return null;
    }
}

// DTOs for Config endpoints
public record AddComposePathRequest(string Path, bool IsReadOnly = false);
public record UpdateComposePathRequest(bool? IsReadOnly, bool? IsEnabled);
public record UpdateSettingRequest(string Value, string? Description = null);
public record UpdateLogLevelRequest(string Value);
public record LogLevelInfo(string Current, IReadOnlyList<string> Available);

// DTOs for Directory Browser
public class DirectoryBrowseResult
{
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<DirectoryBrowseInfo> Directories { get; set; } = new();

    /// <summary>Files in the current directory. Only populated when the caller requests includeFiles.</summary>
    public List<DirectoryBrowseInfo> Files { get; set; } = new();
}

public class DirectoryBrowseInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsAccessible { get; set; }
}
