using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SafePathBD.Web.Common;
using SafePathBD.Web.Models.DTOs.Auth;

namespace SafePathBD.Tests;

public class AuthServiceRegistrationTests
{
    private static RegisterRequest NewRequest(string email = "new.user@example.com", string? phone = null) =>
        new("New User", email, phone, "Str0ng!Passphrase");

    [Fact]
    public async Task RegisterAsync_CreatesTheUserAndAssignsTheDefaultUserRole()
    {
        using var ctx = new AuthTestContext();

        var result = await ctx.Service.RegisterAsync(NewRequest());

        Assert.True(result.Succeeded);

        var roles = await ctx.Service.GetUserRolesAsync(result.UserId);
        Assert.Equal(new[] { RoleNames.User }, roles);
    }

    [Fact]
    public async Task RegisterAsync_NeverStoresThePlainTextPassword()
    {
        using var ctx = new AuthTestContext();

        var result = await ctx.Service.RegisterAsync(NewRequest());
        var stored = await ctx.Db.Users.SingleAsync(u => u.UserId == result.UserId);

        Assert.NotEqual("Str0ng!Passphrase", stored.PasswordHash);
    }

    [Fact]
    public async Task RegisterAsync_NormalisesTheEmailAddress()
    {
        using var ctx = new AuthTestContext();

        var result = await ctx.Service.RegisterAsync(NewRequest("  Mixed.Case@Example.COM "));
        var stored = await ctx.Db.Users.SingleAsync(u => u.UserId == result.UserId);

        Assert.Equal("mixed.case@example.com", stored.Email);
    }

    [Fact]
    public async Task RegisterAsync_MarksTheAccountActiveAndUnverified()
    {
        using var ctx = new AuthTestContext();

        var result = await ctx.Service.RegisterAsync(NewRequest());
        var stored = await ctx.Db.Users.SingleAsync(u => u.UserId == result.UserId);

        Assert.True(stored.IsActive);
        Assert.False(stored.EmailVerified);
    }

    [Fact]
    public async Task RegisterAsync_RejectsAnEmailThatIsAlreadyRegistered()
    {
        using var ctx = new AuthTestContext();
        ctx.AddUser("taken@example.com", "Str0ng!Passphrase");

        var result = await ctx.Service.RegisterAsync(NewRequest("TAKEN@example.com"));

        Assert.Equal(RegisterStatus.EmailAlreadyRegistered, result.Status);
        Assert.Equal(1, await ctx.Db.Users.CountAsync());
    }

    [Fact]
    public async Task RegisterAsync_RejectsAPhoneNumberThatIsAlreadyRegistered()
    {
        using var ctx = new AuthTestContext();
        var existing = ctx.AddUser("first@example.com", "Str0ng!Passphrase");
        existing.Phone = "+8801700000000";
        await ctx.Db.SaveChangesAsync();

        var result = await ctx.Service.RegisterAsync(NewRequest(phone: "+8801700000000"));

        Assert.Equal(RegisterStatus.PhoneAlreadyRegistered, result.Status);
    }

    [Fact]
    public async Task RegisterAsync_FailsWithoutCreatingAUserWhenTheDefaultRoleIsMissing()
    {
        using var ctx = new AuthTestContext(seedRoles: false);

        var result = await ctx.Service.RegisterAsync(NewRequest());

        Assert.Equal(RegisterStatus.DefaultRoleMissing, result.Status);
        Assert.Equal(0, await ctx.Db.Users.CountAsync());
    }
}
