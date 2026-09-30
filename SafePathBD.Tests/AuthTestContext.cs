using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Identity;
using SafePathBD.Web.Common;
using SafePathBD.Web.Data;
using SafePathBD.Web.Models.Entities;
using SafePathBD.Web.Services.Implementations;

namespace SafePathBD.Tests;

/// <summary>
/// Builds an isolated in-memory SafePathDbContext seeded with the same role rows the real database contains.
/// </summary>
internal sealed class AuthTestContext : IDisposable
{
    public AuthTestContext(bool seedRoles = true)
    {
        var options = new DbContextOptionsBuilder<SafePathDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The provider has no transactions; AuthService still opens one against MySQL.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        Db = new SafePathDbContext(options);

        if (seedRoles)
        {
            Db.Roles.AddRange(
                new Roles { RoleId = 1, RoleName = RoleNames.Admin },
                new Roles { RoleId = 2, RoleName = RoleNames.Moderator },
                new Roles { RoleId = 3, RoleName = RoleNames.User });
            Db.SaveChanges();
        }

        PasswordHasher = new PasswordHasher<Users>();
        Service = new AuthService(Db, PasswordHasher, NullLogger<AuthService>.Instance);
    }

    public SafePathDbContext Db { get; }

    public IPasswordHasher<Users> PasswordHasher { get; }

    public AuthService Service { get; }

    public Users AddUser(string email, string password, bool isActive = true, params string[] roles)
    {
        var user = new Users
        {
            FullName = "Test User",
            Email = email,
            IsActive = isActive
        };

        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        Db.Users.Add(user);
        Db.SaveChanges();

        foreach (var roleName in roles)
        {
            var role = Db.Roles.Single(r => r.RoleName == roleName);
            Db.UserRoles.Add(new UserRoles { UserId = user.UserId, RoleId = role.RoleId });
        }

        Db.SaveChanges();
        return user;
    }

    public void Dispose() => Db.Dispose();
}
