using Aiursoft.NugetNinja.GitServerBase.Models;

namespace Aiursoft.GeminiBot.Tests.Helpers;

/// <summary>
/// Helper class for creating test data objects.
/// Uses reflection to set properties from external packages to avoid type reference issues.
/// </summary>
public static class TestDataFactory
{
    public static Repository CreateRepository(int id, string name, string defaultBranch, string cloneUrl, string ownerLogin)
    {
        var repo = new Repository
        {
            Id = id,
            Name = name,
            DefaultBranch = defaultBranch,
            CloneUrl = cloneUrl
        };

        // Set Owner via reflection to avoid type reference issues
        var ownerProp = typeof(Repository).GetProperty("Owner");
        if (ownerProp != null)
        {
            var ownerType = ownerProp.PropertyType;
            var owner = Activator.CreateInstance(ownerType);
            if (owner != null)
            {
                var loginProp = ownerType.GetProperty("Login");
                loginProp?.SetValue(owner, ownerLogin);
                ownerProp.SetValue(repo, owner);
            }
        }

        return repo;
    }
}
