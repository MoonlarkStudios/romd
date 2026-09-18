using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Configuration;
using Romd.Domain.Identity;
using Romd.Persistence.Identity;

namespace Romd.Persistence;

/// <summary>
///     Seeds the database with default roles and admin user on application startup.
/// </summary>
public sealed class AdminSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AdminSeeder> _logger;
    private readonly TimeProvider _timeProvider;

    public AdminSeeder(
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<AdminSeeder> logger)
    {
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<RomdIdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<RomdUser>>();
        var options = scope.ServiceProvider.GetRequiredService<IRomdOptions>();

        // Seed roles first
        await SeedRolesAsync(roleManager, cancellationToken);

        // Seed system user (locked out, never authenticates)
        await SeedSystemUserAsync(userManager, cancellationToken);

        // Then seed default admin user
        await SeedDefaultAdminAsync(userManager, options, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedRolesAsync(RoleManager<RomdIdentityRole> roleManager, CancellationToken ct)
    {
        var roleTypes = Enum.GetValues<RomdRoleType>();

        foreach (var roleType in roleTypes)
        {
            string roleName = roleType.ToString();
            bool exists = await roleManager.RoleExistsAsync(roleName);

            if (!exists)
            {
                var role = new RomdIdentityRole
                {
                    Name = roleName
                };

                var result = await roleManager.CreateAsync(role);

                if (result.Succeeded)
                {
                    _logger.LogInformation("Created role: {RoleName}", roleName);
                }
                else
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    _logger.LogError("Failed to create role {RoleName}: {Errors}", roleName, errors);
                }
            }
        }
    }

    private async Task SeedSystemUserAsync(
        UserManager<RomdUser> userManager,
        CancellationToken ct)
    {
        var existing = await userManager.FindByIdAsync(SystemActor.UserId.ToString());
        if (existing is not null)
            return;

        var systemUser = new RomdUser
        {
            Id = SystemActor.UserId,
            UserName = SystemActor.UserName,
            Email = SystemActor.Email,
            EmailConfirmed = true,
            LockoutEnabled = true,
            LockoutEnd = DateTimeOffset.MaxValue,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        var result = await userManager.CreateAsync(systemUser);

        if (result.Succeeded)
            _logger.LogInformation("Created system user {UserId}", SystemActor.UserId);
        else
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogError("Failed to create system user: {Errors}", errors);
        }
    }

    private async Task SeedDefaultAdminAsync(
        UserManager<RomdUser> userManager,
        IRomdOptions options,
        CancellationToken ct)
    {
        string adminEmail = options.DefaultAdminEmail ?? "admin@localhost";
        string adminPassword = options.DefaultAdminPassword ?? "ChangeMe123!";

        var existingAdmin = await userManager.FindByEmailAsync(adminEmail);
        if (existingAdmin is not null)
        {
            _logger.LogDebug("Admin user {Email} already exists, skipping creation", adminEmail);
            return;
        }

        var adminUser = new RomdUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        var createResult = await userManager.CreateAsync(adminUser, adminPassword);

        if (!createResult.Succeeded)
        {
            var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
            _logger.LogError("Failed to create admin user {Email}: {Errors}", adminEmail, errors);
            return;
        }

        // Assign Admin role
        var roleResult = await userManager.AddToRoleAsync(adminUser, RomdRoleType.Admin.ToString());

        if (roleResult.Succeeded)
        {
            _logger.LogInformation(
                "Created default admin user {Email} with Admin role",
                adminEmail);
        }
        else
        {
            var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
            _logger.LogError("Failed to assign Admin role to {Email}: {Errors}", adminEmail, errors);
        }
    }
}
