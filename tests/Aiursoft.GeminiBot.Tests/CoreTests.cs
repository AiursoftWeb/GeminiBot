using Aiursoft.GeminiBot.Models;
using Aiursoft.GeminiBot.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aiursoft.GeminiBot.Tests;

[TestClass]
public class CoreTests
{
    [TestMethod]
    public void ProcessResult_Succeeded_Works()
    {
        var result = ProcessResult.Succeeded("Test");
        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public void ProcessResult_Failed_Works()
    {
        var result = ProcessResult.Failed("Test");
        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void GeminiBotOptions_HasDefaults()
    {
        var options = new GeminiBotOptions();
        Assert.IsNotNull(options.WorkspaceFolder);
    }
}
