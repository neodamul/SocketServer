using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using SocketLoadTest;

namespace SocketTests.Model;

[TestClass]
public class SocketLoadTestTests
{
    [TestMethod]
    public void ParseMessageTestOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--clients", "20",
                "--batch-size", "5",
                "--hold-seconds", "0",
                "--port", "0",
                "--message-test",
                "--message-rounds", "3"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(20, options.Clients);
        Assert.AreEqual(5, options.BatchSize);
        Assert.AreEqual(0, options.HoldSeconds);
        Assert.AreEqual(0, options.Port);
        Assert.IsTrue(options.MessageTest);
        Assert.AreEqual(3, options.MessageRounds);
    }

    [TestMethod]
    public void ParseMessageTestRejectsValueTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--message-test=true" },
            out _,
            out string error));

        Assert.AreEqual("--message-test does not accept a value.", error);
    }

    [TestMethod]
    public async Task RunMessageLoadTestWithTwoClientsTest()
    {
        int exitCode = await Program.RunAsync(new[]
        {
            "--clients", "2",
            "--batch-size", "2",
            "--hold-seconds", "0",
            "--port", "0",
            "--message-test",
            "--message-rounds", "1"
        });

        Assert.AreEqual(0, exitCode);
    }
}
