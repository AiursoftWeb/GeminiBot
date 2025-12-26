using Aiursoft.NugetNinja.GeminiBot.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.GeminiBot.Tests.Configuration;

[TestClass]
public class GeminiBotOptionsTests
{
    [TestMethod]
    public void DefaultValues_AreSetCorrectly()
    {
        // Act
        var options = new GeminiBotOptions();

        // Assert
        Assert.IsNotNull(options.WorkspaceFolder);
        Assert.IsTrue(options.WorkspaceFolder.Contains("NugetNinjaWorkspace"));
        Assert.AreEqual(TimeSpan.FromMinutes(20), options.GeminiTimeout);
        Assert.AreEqual(5000, options.ForkWaitDelayMs);
        Assert.IsNull(options.Model);
        Assert.IsNull(options.GeminiApiKey);
    }

    [TestMethod]
    public void WorkspaceFolder_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();
        var customPath = "/tmp/custom-workspace";

        // Act
        options.WorkspaceFolder = customPath;

        // Assert
        Assert.AreEqual(customPath, options.WorkspaceFolder);
    }

    [TestMethod]
    public void GeminiTimeout_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();
        var customTimeout = TimeSpan.FromMinutes(30);

        // Act
        options.GeminiTimeout = customTimeout;

        // Assert
        Assert.AreEqual(customTimeout, options.GeminiTimeout);
    }

    [TestMethod]
    public void ForkWaitDelayMs_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();
        var customDelay = 10000;

        // Act
        options.ForkWaitDelayMs = customDelay;

        // Assert
        Assert.AreEqual(customDelay, options.ForkWaitDelayMs);
    }

    [TestMethod]
    public void Model_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();
        var modelName = "gemini-2.0-flash-exp";

        // Act
        options.Model = modelName;

        // Assert
        Assert.AreEqual(modelName, options.Model);
    }

    [TestMethod]
    public void GeminiApiKey_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();
        var apiKey = "test-api-key-12345";

        // Act
        options.GeminiApiKey = apiKey;

        // Assert
        Assert.AreEqual(apiKey, options.GeminiApiKey);
    }
}
