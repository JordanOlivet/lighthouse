using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Lighthouse.Data;
using Lighthouse.DTOs;
using Lighthouse.Models;
using Lighthouse.Services;
using System.Security.Claims;

namespace Lighthouse.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UserGroupsController : BaseController
{
    private readonly AppDbContext _context;
    private readonly ILogger<UserGroupsController> _logger;

    public UserGroupsController(
        AppDbContext context,
        ILogger<UserGroupsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all user groups
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<UserGroupDto>>>> GetAllGroups()
    {
        var groups = await _context.UserGroups
            .Include(g => g.UserGroupMemberships)
            .Select(g => new UserGroupDto
            {
                Id = g.Id,
                Name = g.Name,
                Description = g.Description,
                CreatedAt = g.CreatedAt,
                UpdatedAt = g.UpdatedAt,
                MemberCount = g.UserGroupMemberships.Count,
                MemberIds = g.UserGroupMemberships.Select(m => m.UserId).ToList()
            })
            .ToListAsync();

        return Ok(ApiResponse.Ok(groups));
    }

    /// <summary>
    /// Get a specific user group by ID
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<ApiResponse<UserGroupDto>>> GetGroup(int id)
    {
        var group = await _context.UserGroups
            .Include(g => g.UserGroupMemberships)
            .FirstOrDefaultAsync(g => g.Id == id);

        if (group == null)
        {
            return NotFound(ApiResponse.Fail<UserGroupDto>("User group not found"));
        }

        var dto = new UserGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            CreatedAt = group.CreatedAt,
            UpdatedAt = group.UpdatedAt,
            MemberCount = group.UserGroupMemberships.Count,
            MemberIds = group.UserGroupMemberships.Select(m => m.UserId).ToList()
        };

        return Ok(ApiResponse.Ok(dto));
    }

    /// <summary>
    /// Create a new user group
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<UserGroupDto>>> CreateGroup([FromBody] CreateUserGroupRequest request)
    {
        // Check if group name already exists
        if (await _context.UserGroups.AnyAsync(g => g.Name == request.Name))
        {
            return BadRequest(ApiResponse.Fail<UserGroupDto>("A group with this name already exists"));
        }

        var group = new UserGroup
        {
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        };

        _context.UserGroups.Add(group);
        await _context.SaveChangesAsync();

        // Add members if specified
        if (request.MemberIds.Any())
        {
            foreach (var userId in request.MemberIds)
            {
                var membership = new UserGroupMembership
                {
                    UserId = userId,
                    UserGroupId = group.Id,
                    CreatedAt = DateTime.UtcNow
                };
                _context.UserGroupMemberships.Add(membership);
            }
            await _context.SaveChangesAsync();
        }

        // Create permissions if provided
        if (request.Permissions != null && request.Permissions.Any())
        {
            foreach (var permInput in request.Permissions)
            {
                // Check for duplicate permissions
                var existingPerm = await _context.ResourcePermissions
                    .FirstOrDefaultAsync(p =>
                        p.UserGroupId == group.Id &&
                        p.ResourceType == permInput.ResourceType &&
                        p.ResourceName == permInput.ResourceName);

                if (existingPerm == null)
                {
                    var permission = new ResourcePermission
                    {
                        UserGroupId = group.Id,
                        ResourceType = permInput.ResourceType,
                        ResourceName = permInput.ResourceName,
                        Permissions = permInput.Permissions,
                        CreatedAt = DateTime.UtcNow
                    };
                    _context.ResourcePermissions.Add(permission);
                }
            }
            await _context.SaveChangesAsync();
        }

        _logger.LogInformation("User group created: {GroupName} with {PermissionCount} permissions", group.Name, request.Permissions?.Count ?? 0);

        var dto = new UserGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            CreatedAt = group.CreatedAt,
            UpdatedAt = group.UpdatedAt,
            MemberCount = request.MemberIds.Count,
            MemberIds = request.MemberIds
        };

        return CreatedAtAction(nameof(GetGroup), new { id = group.Id }, ApiResponse.Ok(dto));
    }

    /// <summary>
    /// Update an existing user group
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<ApiResponse<UserGroupDto>>> UpdateGroup(int id, [FromBody] UpdateUserGroupRequest request)
    {
        var group = await _context.UserGroups
            .Include(g => g.UserGroupMemberships)
            .FirstOrDefaultAsync(g => g.Id == id);

        if (group == null)
        {
            return NotFound(ApiResponse.Fail<UserGroupDto>("User group not found"));
        }

        // Check if new name conflicts with existing group
        if (group.Name != request.Name && await _context.UserGroups.AnyAsync(g => g.Name == request.Name))
        {
            return BadRequest(ApiResponse.Fail<UserGroupDto>("A group with this name already exists"));
        }

        group.Name = request.Name;
        group.Description = request.Description;
        group.UpdatedAt = DateTime.UtcNow;

        // Update permissions if provided
        if (request.Permissions != null)
        {
            // Remove all existing group permissions
            var existingPermissions = await _context.ResourcePermissions
                .Where(p => p.UserGroupId == id)
                .ToListAsync();
            _context.ResourcePermissions.RemoveRange(existingPermissions);

            // Add new permissions
            foreach (var permInput in request.Permissions)
            {
                var permission = new ResourcePermission
                {
                    UserGroupId = id,
                    ResourceType = permInput.ResourceType,
                    ResourceName = permInput.ResourceName,
                    Permissions = permInput.Permissions,
                    CreatedAt = DateTime.UtcNow
                };
                _context.ResourcePermissions.Add(permission);
            }
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("User group updated: {GroupName} with {PermissionCount} permissions", group.Name, request.Permissions?.Count ?? 0);

        var dto = new UserGroupDto
        {
            Id = group.Id,
            Name = group.Name,
            Description = group.Description,
            CreatedAt = group.CreatedAt,
            UpdatedAt = group.UpdatedAt,
            MemberCount = group.UserGroupMemberships.Count,
            MemberIds = group.UserGroupMemberships.Select(m => m.UserId).ToList()
        };

        return Ok(ApiResponse.Ok(dto));
    }

    /// <summary>
    /// Delete a user group
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteGroup(int id)
    {
        var group = await _context.UserGroups.FindAsync(id);

        if (group == null)
        {
            return NotFound(ApiResponse.Fail<object>("User group not found"));
        }

        _context.UserGroups.Remove(group);
        await _context.SaveChangesAsync();
        _logger.LogInformation("User group deleted: {GroupName}", group.Name);

        return Ok(ApiResponse.Ok<object?>(null, "User group deleted successfully"));
    }

    /// <summary>
    /// Add a user to a group
    /// </summary>
    [HttpPost("{id}/members")]
    public async Task<ActionResult<ApiResponse<object>>> AddUserToGroup(int id, [FromBody] AddUserToGroupRequest request)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null)
        {
            return NotFound(ApiResponse.Fail<object>("User group not found"));
        }

        var user = await _context.Users.FindAsync(request.UserId);
        if (user == null)
        {
            return NotFound(ApiResponse.Fail<object>("User not found"));
        }

        // Check if membership already exists
        if (await _context.UserGroupMemberships.AnyAsync(m => m.UserId == request.UserId && m.UserGroupId == id))
        {
            return BadRequest(ApiResponse.Fail<object>("User is already a member of this group"));
        }

        var membership = new UserGroupMembership
        {
            UserId = request.UserId,
            UserGroupId = id,
            CreatedAt = DateTime.UtcNow
        };

        _context.UserGroupMemberships.Add(membership);
        await _context.SaveChangesAsync();
        _logger.LogInformation("User {MemberUsername} added to group {GroupName}", user.Username, group.Name);

        return Ok(ApiResponse.Ok<object?>(null, "User added to group successfully"));
    }

    /// <summary>
    /// Remove a user from a group
    /// </summary>
    [HttpDelete("{id}/members/{userId}")]
    public async Task<ActionResult<ApiResponse<object>>> RemoveUserFromGroup(int id, int userId)
    {
        var membership = await _context.UserGroupMemberships
            .Include(m => m.UserGroup)
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.UserId == userId && m.UserGroupId == id);

        if (membership == null)
        {
            return NotFound(ApiResponse.Fail<object>("User is not a member of this group"));
        }

        _context.UserGroupMemberships.Remove(membership);
        await _context.SaveChangesAsync();
        _logger.LogInformation("User {MemberUsername} removed from group {GroupName}", membership.User?.Username, membership.UserGroup?.Name);

        return Ok(ApiResponse.Ok<object?>(null, "User removed from group successfully"));
    }

    /// <summary>
    /// Get all members of a group
    /// </summary>
    [HttpGet("{id}/members")]
    public async Task<ActionResult<ApiResponse<List<UserDto>>>> GetGroupMembers(int id)
    {
        var group = await _context.UserGroups.FindAsync(id);
        if (group == null)
        {
            return NotFound(ApiResponse.Fail<List<UserDto>>("User group not found"));
        }

        var members = await _context.UserGroupMemberships
            .Where(m => m.UserGroupId == id)
            .Include(m => m.User)
            .ThenInclude(u => u!.Role)
            .Select(m => new UserDto(
                m.User!.Id,
                m.User.Username,
                m.User.Email,
                m.User.Role!.Name,
                m.User.IsEnabled,
                m.User.MustChangePassword,
                m.User.MustAddEmail,
                m.User.CreatedAt,
                m.User.LastLoginAt,
                m.User.OidcSubject != null,
                m.User.PasswordHash != null
            ))
            .ToListAsync();

        return Ok(ApiResponse.Ok(members));
    }
}
