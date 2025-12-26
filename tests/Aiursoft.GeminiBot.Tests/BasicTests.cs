using Aiursoft.NugetNinja.GeminiBot.Configuration;
using Aiursoft.NugetNinja.GeminiBot.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.GeminiBot.Tests;

/// <summary>
/// Basic smoke tests to ensure the project compiles and basic functionality works.
/// More comprehensive tests can be added as we understand the external dependencies better.
/// </summary>
[TestClass]
public class BasicTests
{
    [TestMethod]
    public void ProcessResult_Succeeded_CreatesSuccessResult()
    {
        // Arrange & Act
        var result = ProcessResult.Succeeded("Test succeeded");

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Test succeeded", result.Message);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void ProcessResult_Failed_CreatesFailedResult()
    {
        // Arrange & Act
        var result = ProcessResult.Failed("Test failed");

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Test failed", result.Message);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void ProcessResult_Failed_WithException_IncludesException()
    {
        // Arrange
        var exception = new InvalidOperationException("Error details");

        // Act
        var result = ProcessResult.Failed("Test failed", exception);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual("Test failed", result.Message);
        Assert.AreEqual(exception, result.Error);
    }

    [TestMethod]
    public void ProcessResult_Skipped_CreatesSuccessWithSkippedMessage()
    {
        // Arrange & Act
        var result = ProcessResult.Skipped("Already processed");

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual("Skipped: Already processed", result.Message);
    }

    [TestMethod]
    public void ProcessResult_ToString_Success_FormatsCorrectly()
    {
        // Arrange
        var result = ProcessResult.Succeeded("Done");

        // Act
        var stringResult = result.ToString();

        // Assert
        Assert.AreEqual("Success: Done", stringResult);
    }

    [TestMethod]
    public void ProcessResult_ToString_Failed_FormatsCorrectly()
    {
        // Arrange
        var result = ProcessResult.Failed("Error");

        // Act
        var stringResult = result.ToString();

        // Assert
        Assert.AreEqual("Failed: Error", stringResult);
    }

    [TestMethod]
    public void GeminiBotOptions_DefaultValues_AreCorrect()
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
    public void GeminiBotOptions_Properties_CanBeSet()
    {
        // Arrange
        var options = new GeminiBotOptions();

        // Act
        options.WorkspaceFolder = "/tmp/test";
        options.GeminiTimeout = TimeSpan.FromMinutes(30);
        options.ForkWaitDelayMs = 10000;
        options.Model = "gemini-2.0-flash";
        options.GeminiApiKey = "test-key";

        // Assert
        Assert.AreEqual("/tmp/test", options.WorkspaceFolder);
        Assert.AreEqual(TimeSpan.FromMinutes(30), options.GeminiTimeout);
        Assert.AreEqual(10000, options.ForkWaitDelayMs);
        Assert.AreEqual("gemini-2.0-flash", options.Model);
        Assert.AreEqual("test-key", options.GeminiApiKey);
    }
}
