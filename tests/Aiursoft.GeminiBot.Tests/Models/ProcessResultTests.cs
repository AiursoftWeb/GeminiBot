using Aiursoft.NugetNinja.GeminiBot.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.GeminiBot.Tests.Models;

[TestClass]
public class ProcessResultTests
{
    [TestMethod]
    public void Succeeded_CreatesSuccessResult()
    {
        // Arrange
        var message = "Operation completed";

        // Act
        var result = ProcessResult.Succeeded(message);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual(message, result.Message);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Failed_CreatesFailedResult_WithoutException()
    {
        // Arrange
        var message = "Operation failed";

        // Act
        var result = ProcessResult.Failed(message);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual(message, result.Message);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Failed_CreatesFailedResult_WithException()
    {
        // Arrange
        var message = "Operation failed";
        var exception = new InvalidOperationException("Test error");

        // Act
        var result = ProcessResult.Failed(message, exception);

        // Assert
        Assert.IsFalse(result.Success);
        Assert.AreEqual(message, result.Message);
        Assert.AreEqual(exception, result.Error);
    }

    [TestMethod]
    public void Skipped_CreatesSuccessResultWithSkippedMessage()
    {
        // Arrange
        var reason = "Already processed";

        // Act
        var result = ProcessResult.Skipped(reason);

        // Assert
        Assert.IsTrue(result.Success);
        Assert.AreEqual($"Skipped: {reason}", result.Message);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void ToString_FormatsSuccessCorrectly()
    {
        // Arrange
        var result = ProcessResult.Succeeded("Done");

        // Act
        var str = result.ToString();

        // Assert
        Assert.AreEqual("Success: Done", str);
    }

    [TestMethod]
    public void ToString_FormatsFailureCorrectly()
    {
        // Arrange
        var result = ProcessResult.Failed("Error occurred");

        // Act
        var str = result.ToString();

        // Assert
        Assert.AreEqual("Failed: Error occurred", str);
    }
}
