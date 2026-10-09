using DockerComposeManager.Services.Security;
using Lighthouse.Configuration;
using Lighthouse.Services;
using Lighthouse.Services.LogStreaming;

namespace Lighthouse.Extensions;

/// <summary>
/// Groups the application's dependency-injection registrations into cohesive,
/// per-subsystem extension methods so that Program.cs stays a thin composition
/// root. These only move registrations out of Program.cs — service lifetimes
/// and ordering are preserved exactly.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds the strongly-typed options classes to their configuration sections.
    /// </summary>
    public static IServiceCollection AddAppOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PasswordHashingOptions>(configuration.GetSection(PasswordHashingOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PasswordResetOptions>(configuration.GetSection(PasswordResetOptions.SectionName));
        services.Configure<ComposeDiscoveryOptions>(configuration.GetSection("ComposeDiscovery"));
        services.Configure<SelfUpdateOptions>(configuration.GetSection("SelfUpdate"));
        services.Configure<MaintenanceOptions>(configuration.GetSection("Maintenance"));
        services.Configure<UpdateCheckOptions>(configuration.GetSection("UpdateCheck"));
        return services;
    }

    /// <summary>
    /// Core application services: auth, users, permissions, Docker and
    /// the compose domain.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

        services.AddScoped<JwtTokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ComposeService>();
        services.AddScoped<IOperationService, OperationService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddSingleton<DockerService>();
        services.AddSingleton<IDockerImageOperations>(sp => sp.GetRequiredService<DockerService>());
        services.AddSingleton<IContainerLogService, ContainerLogService>();
        services.AddSingleton<IAppLogService, AppLogService>();
        services.AddTransient<ComposeLogStreamCoordinator>();
        services.AddScoped<IImageService, ImageService>();
        services.AddScoped<IRegistryCredentialService, RegistryCredentialService>();
        return services;
    }

    /// <summary>
    /// Docker Compose execution and discovery services.
    /// </summary>
    public static IServiceCollection AddComposeServices(this IServiceCollection services)
    {
        services.AddSingleton<DockerCommandExecutorService>();
        services.AddSingleton<IComposeEnvFileResolver, ComposeEnvFileResolver>();
        services.AddSingleton<IComposeHostPathService, ComposeHostPathService>();
        services.AddScoped<IComposeDiscoveryService, ComposeDiscoveryService>();
        services.AddScoped<IComposeOperationService, ComposeOperationService>();

        services.AddScoped<IComposeFileScanner, ComposeFileScannerService>();
        services.AddScoped<IPathValidator, PathValidatorService>();
        services.AddScoped<IComposeFileCacheService, ComposeFileCacheService>();
        services.AddScoped<IPathMappingService, PathMappingService>();
        services.AddScoped<IProjectMatchingService, ProjectMatchingService>();
        services.AddScoped<IConflictResolutionService, ConflictResolutionService>();
        services.AddScoped<IComposeFileEditorService, ComposeFileEditorService>();
        // Note: ComposeCommandClassifier is static, no DI registration needed.
        return services;
    }

    /// <summary>
    /// Application self-update (GitHub release based) services.
    /// </summary>
    public static IServiceCollection AddSelfUpdateServices(this IServiceCollection services)
    {
        services.AddHttpClient<IGitHubReleaseService, GitHubReleaseService>();
        services.AddSingleton<IImageReferenceParser, ImageReferenceParser>();
        services.AddSingleton<IComposeFileDetectorService, ComposeFileDetectorService>();
        services.AddSingleton<IVersionDetectionService, VersionDetectionService>();
        services.AddSingleton<ISelfFilterService, SelfFilterService>();
        services.AddSingleton<IInstanceIdentifierService, InstanceIdentifierService>();
        services.AddScoped<ISelfUpdateService, SelfUpdateService>();
        return services;
    }

    /// <summary>
    /// Container/compose image-update services: registry clients, digest checks
    /// and the update caches.
    /// </summary>
    public static IServiceCollection AddImageUpdateServices(this IServiceCollection services)
    {
        services.AddHttpClient<Lighthouse.Services.Registry.DockerHubRegistryClient>();
        services.AddHttpClient<Lighthouse.Services.Registry.GhcrRegistryClient>();
        services.AddHttpClient<Lighthouse.Services.Registry.GenericOciRegistryClient>();
        services.AddScoped<Lighthouse.Services.Registry.IRegistryClient, Lighthouse.Services.Registry.DockerHubRegistryClient>();
        services.AddScoped<Lighthouse.Services.Registry.IRegistryClient, Lighthouse.Services.Registry.GhcrRegistryClient>();
        services.AddScoped<Lighthouse.Services.Registry.IRegistryClient, Lighthouse.Services.Registry.GenericOciRegistryClient>();
        services.AddScoped<Lighthouse.Services.Registry.IRegistryClientFactory, Lighthouse.Services.Registry.RegistryClientFactory>();
        // Shared across scoped registry clients and successive check cycles → singleton.
        services.AddSingleton<Lighthouse.Services.Registry.IRegistryRateLimitGate, Lighthouse.Services.Registry.RegistryRateLimitGate>();
        services.AddScoped<IImageDigestService, ImageDigestService>();
        // Shared interval published by the periodic check and consumed by the cache service → singleton.
        services.AddSingleton<UpdateCheckIntervalState>();
        services.AddSingleton<IImageUpdateCacheService, ImageUpdateCacheService>();
        services.AddSingleton<IContainerUpdateCacheService, ContainerUpdateCacheService>();
        services.AddSingleton<DockerPullProgressParser>();
        services.AddScoped<IComposeUpdateChecker, ComposeUpdateChecker>();
        services.AddScoped<IComposeUpdateService, ComposeUpdateService>();
        services.AddScoped<IContainerUpdateService, ContainerUpdateService>();
        return services;
    }

    /// <summary>
    /// Outbound notification services (Discord webhook).
    /// </summary>
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        services.AddHttpClient<Lighthouse.Services.Notifications.INotificationService,
            Lighthouse.Services.Notifications.DiscordNotificationService>();
        return services;
    }

    /// <summary>
    /// Real-time (SSE) connection management and Docker event handling.
    /// </summary>
    public static IServiceCollection AddRealtimeServices(this IServiceCollection services)
    {
        services.AddSingleton<SseConnectionManagerService>();
        services.AddSingleton<CrashLoopDetectionService>();
        services.AddSingleton<IDockerEventBus, DockerEventBus>();
        services.AddSingleton<DockerEventHandlerService>();
        return services;
    }

    /// <summary>
    /// Long-running hosted background services.
    /// </summary>
    public static IServiceCollection AddAppBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<DockerEventsMonitorService>();
        services.AddHostedService<ComposeDiscoveryInitializer>();
        services.AddHostedService<ProjectUpdateCheckBackgroundService>();
        services.AddHostedService<AutoUpdateComposeBackgroundService>();
        services.AddHostedService<AutoUpdateAppBackgroundService>();
        services.AddHostedService<AutoPruneImagesBackgroundService>();
        services.AddHostedService<AppUpdateNotificationStartupService>();
        services.AddHostedService<Lighthouse.BackgroundServices.CleanupBackgroundService>();
        return services;
    }
}
