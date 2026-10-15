using Microsoft.AspNetCore.Http;
using MortalityReporting.ImportApi.Configuration;
using Xunit;

namespace MortalityReporting.ImportApi.Tests;

public sealed class BasicAuthorizationTests
{
    [Fact]
    public void AcceptsCorrectBasicCredentials()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("api-user:api-pass"));
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void RejectsIncorrectBasicCredentials()
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("api-user:wrong"));
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "api-pass" };
        Assert.False(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void EmptyUsernameBypassesAuthorization()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = "", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void NullUsernameBypassesAuthorization()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = null, IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void WhitespaceUsernameBypassesAuthorization()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = "   ", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void EmptyUsernameBypassesAuthorizationWithNoAuthHeader()
    {
        DefaultHttpContext context = new();
        // No Authorization header set
        ImportApiOptions options = new() { IncomingUsername = "", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void NullUsernameBypassesAuthorizationWithNoAuthHeader()
    {
        DefaultHttpContext context = new();
        // No Authorization header set
        ImportApiOptions options = new() { IncomingUsername = null, IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void WhitespaceUsernameBypassesAuthorizationWithNoAuthHeader()
    {
        DefaultHttpContext context = new();
        // No Authorization header set
        ImportApiOptions options = new() { IncomingUsername = "   ", IncomingPassword = "api-pass" };
        Assert.True(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void ConfiguredUsernameWithEmptyPasswordRejected()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "" };
        Assert.False(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void ConfiguredUsernameWithNullPasswordRejected()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = null };
        Assert.False(BasicAuthorization.IsValid(context.Request, options));
    }

    [Fact]
    public void ConfiguredUsernameWithWhitespacePasswordRejected()
    {
        DefaultHttpContext context = new();
        ImportApiOptions options = new() { IncomingUsername = "api-user", IncomingPassword = "   " };
        Assert.False(BasicAuthorization.IsValid(context.Request, options));
    }
}
