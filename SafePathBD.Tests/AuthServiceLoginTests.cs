using System.Security.Claims;
using SafePathBD.Web.Common;
using SafePathBD.Web.Models.DTOs.Auth;

namespace SafePathBD.Tests;

public class AuthServiceLoginTests
{
    [Fact]
    public async Task ValidateCredentialsAsync_SucceedsForCorrectCredentials()
    {
        using var ctx = new AuthTestContext();
        ctx.AddUser("driver@example.com", "Str0ng!Passphrase", roles: RoleNames.User);

        var result = await ctx.Service.ValidateCredentialsAsync("driver@example.com", "Str0ng!Passphrase");

        Assert.Equal(LoginStatus.Success, result.Status);
        Assert.NotNull(result.User);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_IsCaseInsensitiveForTheEmail()
    {
        using var ctx = new AuthTestContext();
        ctx.AddUser("driver@example.com", "Str0ng!Passphrase");

        var result = await ctx.Service.ValidateCredentialsAsync("  DRIVER@Example.com ", "Str0ng!Passphrase");

        Assert.Equal(LoginStatus.Success, result.Status);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_FailsForTheWrongPassword()
    {
        using var ctx = new AuthTestContext();
        ctx.AddUser("driver@example.com", "Str0ng!Passphrase");

        var result = await ctx.Service.ValidateCredentialsAsync("driver@example.com", "not-the-password");

        Assert.Equal(LoginStatus.InvalidCredentials, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_FailsForAnUnknownEmail()
    {
        using var ctx = new AuthTestContext();

        var result = await ctx.Service.ValidateCredentialsAsync("nobody@example.com", "Str0ng!Passphrase");

        Assert.Equal(LoginStatus.InvalidCredentials, result.Status);
    }

    [Fact]
    public async Task ValidateCredentialsAsync_RejectsADeactivatedAccountBeforeCheckingThePassword()
    {
        using var ctx = new AuthTestContext();
        ctx.AddUser("disabled@example.com", "Str0ng!Passphrase", isActive: false);

        var result = await ctx.Service.ValidateCredentialsAsync("disabled@example.com", "Str0ng!Passphrase");

        Assert.Equal(LoginStatus.AccountDisabled, result.Status);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task BuildPrincipalAsync_AddsOneRoleClaimPerAssignedRole()
    {
        using var ctx = new AuthTestContext();
        var user = ctx.AddUser("mod@example.com", "Str0ng!Passphrase", roles: new[] { RoleNames.Moderator, RoleNames.User });

        var principal = await ctx.Service.BuildPrincipalAsync(user);

        Assert.True(principal.IsInRole(RoleNames.Moderator));
        Assert.True(principal.IsInRole(RoleNames.User));
        Assert.False(principal.IsInRole(RoleNames.Admin));
        Assert.Equal(2, principal.FindAll(ClaimTypes.Role).Count());
    }

    [Fact]
    public async Task BuildPrincipalAsync_CarriesIdentityClaimsButNotThePasswordHash()
    {
        using var ctx = new AuthTestContext();
        var user = ctx.AddUser("claims@example.com", "Str0ng!Passphrase", roles: RoleNames.User);

        var principal = await ctx.Service.BuildPrincipalAsync(user);

        Assert.Equal(user.UserId.ToString(), principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("claims@example.com", principal.FindFirstValue(ClaimTypes.Email));
        Assert.DoesNotContain(principal.Claims, c => c.Value == user.PasswordHash);
    }

    [Fact]
    public async Task RecordSuccessfulLoginAsync_StampsLastLoginAt()
    {
        using var ctx = new AuthTestContext();
        var user = ctx.AddUser("stamp@example.com", "Str0ng!Passphrase");

        Assert.Null(user.LastLoginAt);

        await ctx.Service.RecordSuccessfulLoginAsync(user.UserId);

        Assert.NotNull(ctx.Db.Users.Single(u => u.UserId == user.UserId).LastLoginAt);
    }
}
