using Lighthouse.DTOs;
using Lighthouse.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lighthouse.Controllers;

/// <summary>
/// User management endpoints (Admin only)
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "admin")]
public class UsersController : BaseController
{
    private readonly IUserService _userService;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserService userService, ILogger<UsersController> logger)
    {
        _userService = userService;
        _logger = logger;
    }

    /// <summary>
    /// Get all users with pagination, filtering, and sorting
    /// </summary>
    /// <param name="pageNumber">Page number (default: 1)</param>
    /// <param name="pageSize">Page size (default: 20, max: 100)</param>
    /// <param name="search">Search term for username</param>
    /// <param name="role">Filter by role</param>
    /// <param name="isEnabled">Filter by enabled status</param>
    /// <param name="sortBy">Sort field (username, createdAt, lastLoginAt)</param>
    /// <param name="sortDescending">Sort descending (default: false)</param>
    /// <returns>Paginated list of users</returns>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<UserDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ApiResponse<PaginatedResponse<UserDto>>>> GetAllUsers(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? role = null,
        [FromQuery] bool? isEnabled = null,
        [FromQuery] string? sortBy = "username",
        [FromQuery] bool sortDescending = false)
    {
        try
        {
            // Validate pagination parameters
            if (pageNumber < 1) pageNumber = 1;
            if (pageSize < 1) pageSize = 20;
            if (pageSize > 100) pageSize = 100;

            var paginatedResult = await _userService.GetAllUsersAsync(
                pageNumber,
                pageSize,
                search,
                role,
                isEnabled,
                sortBy,
                sortDescending
            );

            return Ok(ApiResponse.Ok(paginatedResult, "Users retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving users");
            return StatusCode(500, ApiResponse.Fail<PaginatedResponse<UserDto>>("Failed to retrieve users"));
        }
    }

    /// <summary>
    /// Get specific user by ID
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>User details</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> GetUser(int id)
    {
        try
        {
            var user = await _userService.GetUserByIdAsync(id);
            if (user == null)
                return NotFound(ApiResponse.Fail<UserDto>($"User with ID {id} not found"));

            return Ok(ApiResponse.Ok(user, "User retrieved successfully"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user {UserId}", id);
            return StatusCode(500, ApiResponse.Fail<UserDto>("Failed to retrieve user"));
        }
    }

    /// <summary>
    /// Create new user
    /// </summary>
    /// <param name="request">User creation request</param>
    /// <returns>Created user</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<UserDto>>> CreateUser([FromBody] CreateUserRequest request)
    {
        // Validation is handled by CreateUserRequestValidator (FluentValidation). Domain
        // failures (duplicate username, invalid role) surface as typed AppExceptions and
        // are mapped to the right status by ErrorHandlingMiddleware.
        var user = await _userService.CreateUserAsync(request);
        return CreatedAtAction(
            nameof(GetUser),
            new { id = user.Id },
            ApiResponse.Ok(user, "User created successfully")
        );
    }

    /// <summary>
    /// Update user
    /// </summary>
    /// <param name="id">User ID</param>
    /// <param name="request">User update request</param>
    /// <returns>Updated user</returns>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        // Validation is handled by UpdateUserRequestValidator (FluentValidation). Domain
        // failures surface as typed AppExceptions mapped by ErrorHandlingMiddleware
        // (404 not found, 409 conflict, 400 bad request) — no manual status juggling.
        var user = await _userService.UpdateUserAsync(id, request);
        return Ok(ApiResponse.Ok(user, "User updated successfully"));
    }

    /// <summary>
    /// Delete user
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Success message</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<object>>> DeleteUser(int id)
    {
        await _userService.DeleteUserAsync(id);
        return Ok(ApiResponse.Ok<object>(null, "User deleted successfully"));
    }

    /// <summary>
    /// Enable user account
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Updated user</returns>
    [HttpPut("{id}/enable")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> EnableUser(int id)
    {
        var user = await _userService.EnableUserAsync(id);
        return Ok(ApiResponse.Ok(user, "User enabled successfully"));
    }

    /// <summary>
    /// Disable user account
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Updated user</returns>
    [HttpPut("{id}/disable")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> DisableUser(int id)
    {
        var user = await _userService.DisableUserAsync(id);
        return Ok(ApiResponse.Ok(user, "User disabled successfully"));
    }

    /// <summary>
    /// Remove the SSO (OIDC) identity link of a user account
    /// </summary>
    /// <param name="id">User ID</param>
    /// <returns>Updated user</returns>
    [HttpDelete("{id}/oidc-link")]
    [ProducesResponseType(typeof(ApiResponse<UserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiResponse<UserDto>>> UnlinkOidc(int id)
    {
        var user = await _userService.UnlinkOidcAsync(id);
        return Ok(ApiResponse.Ok(user, "SSO link removed"));
    }
}
