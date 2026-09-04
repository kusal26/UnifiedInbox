using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using UnifiedInbox.Infrastructure.Configuration;

namespace UnifiedInbox.Api.Tests;

/// <summary>
/// Unit coverage for the shared <see cref="ProductionGuard"/> used by both the API and the worker,
/// plus two host-boot proofs that the real API actually refuses a fake provider in Production and
/// starts with a syntactically valid Production configuration.
/// </summary>
public sealed class ProductionConfigurationTests
{
    private static readonly string ValidKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    private const string DevJwt = "development-only-signing-key-change-before-production";
    private const string ProdJwt = "a-real-production-signing-key-that-is-long-enough-42";

    [Fact]
    public void Non_production_environment_is_never_validated()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["WhatsApp:UseFake"] = "true",
            ["WhatsApp:AppSecret"] = "",
            ["Credentials:MasterKey"] = "",
        });
        Should.NotThrow(() => ProductionGuard.Validate(config, isProduction: false));
    }

    [Fact]
    public void Fake_provider_mode_is_rejected_in_production()
    {
        var config = Valid();
        config["WhatsApp:UseFake"] = "true";
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(config), true)).Message.ShouldContain("Fake WhatsApp provider mode");
    }

    [Fact]
    public void Missing_meta_app_secrets_are_rejected_in_production()
    {
        var cases = new (string Key, string Expected)[]
        {
            ("WhatsApp:AppId", "WhatsApp:AppId is required"),
            ("WhatsApp:EmbeddedSignupConfigId", "WhatsApp:EmbeddedSignupConfigId is required"),
            ("WhatsApp:AppSecret", "WhatsApp:AppSecret is required"),
            ("WhatsApp:VerifyToken", "WhatsApp:VerifyToken is required"),
            ("Credentials:MasterKey", "Credentials:MasterKey is required"),
        };
        foreach (var (key, expected) in cases)
        {
            var config = Valid();
            config.Remove(key);
            Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(config), true)).Message.ShouldContain(expected);
        }
    }

    [Fact]
    public void Invalid_credential_keys_are_rejected_in_production()
    {
        var notBase64 = Valid();
        notBase64["Credentials:MasterKey"] = "not-valid-base64!!!";
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(notBase64), true)).Message.ShouldContain("must be valid base64");

        var wrongLength = Valid();
        wrongLength["Credentials:MasterKey"] = Convert.ToBase64String(new byte[16]);
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(wrongLength), true)).Message.ShouldContain("must decode to exactly 32 bytes");

        var badPrevious = Valid();
        badPrevious["Credentials:PreviousMasterKey"] = Convert.ToBase64String(new byte[16]);
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(badPrevious), true)).Message.ShouldContain("Credentials:PreviousMasterKey must decode to exactly 32 bytes");
    }

    [Fact]
    public void Weak_jwt_signing_keys_are_rejected_in_production()
    {
        var shortKey = Valid();
        shortKey["Jwt:SigningKey"] = "short";
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(shortKey), true)).Message.ShouldContain("Jwt:SigningKey");

        var devFallback = Valid();
        devFallback["Jwt:SigningKey"] = DevJwt;
        Should.Throw<InvalidOperationException>(() => ProductionGuard.Validate(Config(devFallback), true)).Message.ShouldContain("Jwt:SigningKey");
    }

    [Fact]
    public void Complete_production_configuration_passes()
    {
        Should.NotThrow(() => ProductionGuard.Validate(Config(Valid()), true));
    }

    [Fact]
    public void Api_host_boot_refuses_fake_provider_in_production()
    {
        using var factory = ApiFactory();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var pair in Valid()) builder.UseSetting(pair.Key, pair.Value!);
            builder.UseSetting("WhatsApp:UseFake", "true"); // last wins: fake must be refused
        });
        var failure = CaptureStartup(host);
        failure.ShouldNotBeNull();
        Innermost(failure).Message.ShouldContain("Fake WhatsApp provider mode");
    }

    [Fact]
    public async Task Api_host_boot_succeeds_with_valid_production_configuration()
    {
        using var host = ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            foreach (var pair in Valid()) builder.UseSetting(pair.Key, pair.Value!);
            builder.UseSetting("WhatsApp:UseFake", "false");
        });
        using var client = host.CreateClient();
        var health = await client.GetAsync("/api/v1/operations/health");
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> ApiFactory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedInbox.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository root was not found.");
        var contentRoot = Path.Combine(directory.FullName, "src", "backend", "UnifiedInbox.Api");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
    }

    private static Exception? CaptureStartup(WebApplicationFactory<Program> host)
    {
        try
        {
            _ = host.CreateClient();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception Innermost(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception;
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["WhatsApp:UseFake"] = "false",
        ["WhatsApp:AppId"] = "111222333444555",
        ["WhatsApp:EmbeddedSignupConfigId"] = "config-embedded-signup",
        ["WhatsApp:AppSecret"] = "unit-test-app-secret",
        ["WhatsApp:VerifyToken"] = "unit-test-verify-token",
        ["Credentials:MasterKey"] = ValidKey,
        ["Jwt:SigningKey"] = ProdJwt,
    };

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
