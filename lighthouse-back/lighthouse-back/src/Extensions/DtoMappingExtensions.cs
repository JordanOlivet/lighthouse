using Lighthouse.DTOs;
using Lighthouse.Models;

namespace Lighthouse.Extensions;

/// <summary>
/// Extension methods for mapping domain models to DTOs
/// </summary>
public static class DtoMappingExtensions
{
    /// <summary>
    /// Convert User entity to UserDto
    /// </summary>
    public static UserDto ToDto(this User user)
    {
        return new UserDto(
            user.Id,
            user.Username,
            user.Email,
            user.Role?.Name ?? "user",
            user.IsEnabled,
            user.MustChangePassword,
            user.MustAddEmail,
            user.CreatedAt,
            user.LastLoginAt,
            OidcLinked: user.OidcSubject != null,
            HasPassword: user.PasswordHash != null
        );
    }

    /// <summary>
    /// Convert Operation entity to OperationDto
    /// </summary>
    public static OperationDto ToDto(this Operation operation)
    {
        return new OperationDto(
            operation.OperationId,
            operation.Type,
            operation.Status,
            operation.Progress,
            operation.ProjectName,
            operation.ProjectPath,
            operation.ContainerId,
            operation.ContainerName,
            operation.User?.Username,
            operation.StartedAt,
            operation.CompletedAt,
            operation.ErrorMessage,
            operation.IsAcknowledged
        );
    }

    /// <summary>
    /// Convert list of Users to list of UserDtos
    /// </summary>
    public static List<UserDto> ToDtoList(this IEnumerable<User> users)
    {
        return users.Select(u => u.ToDto()).ToList();
    }

    /// <summary>
    /// Convert list of Operations to list of OperationDtos
    /// </summary>
    public static List<OperationDto> ToDtoList(this IEnumerable<Operation> operations)
    {
        return operations.Select(o => o.ToDto()).ToList();
    }
}
