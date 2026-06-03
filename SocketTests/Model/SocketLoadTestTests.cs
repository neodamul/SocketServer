using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Text.Json;
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
                "--message-rounds", "3",
                "--ramp-delay-ms", "5",
                "--expected-connected", "10",
                "--healthcheck-timeout-seconds", "3",
                "--message-timeout-seconds", "4"
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
        Assert.AreEqual(5, options.RampDelayMilliseconds);
        Assert.AreEqual(10, options.ExpectedConnected);
        Assert.AreEqual(3, options.HealthCheckTimeoutSeconds);
        Assert.AreEqual(4, options.MessageTimeoutSeconds);
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
    public void ParseLoadTestProfileOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--profile", "soak-10k",
                "--hold-seconds", "1",
                "--report-file", "load-report.json"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual("soak-10k", options.Profile);
        Assert.AreEqual(10000, options.Clients);
        Assert.AreEqual(100, options.BatchSize);
        Assert.AreEqual(1, options.HoldSeconds);
        Assert.AreEqual(10000, options.ExpectedConnected);
        Assert.AreEqual("load-report.json", options.ReportFile);
    }

    [TestMethod]
    public void ParseLoadTestProfileRejectsUnknownProfileTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--profile", "unknown" },
            out _,
            out string error));

        Assert.AreEqual("Unknown profile: unknown", error);
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

    [TestMethod]
    public async Task RunLoadTestWritesSummaryReportTest()
    {
        string reportFile = Path.Combine(Path.GetTempPath(), $"socket-load-report-{Guid.NewGuid():N}.json");
        try
        {
            int exitCode = await Program.RunAsync(new[]
            {
                "--clients", "1",
                "--batch-size", "1",
                "--hold-seconds", "0",
                "--port", "0",
                "--report-file", reportFile
            });

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(File.Exists(reportFile));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(reportFile));
            JsonElement root = document.RootElement;
            Assert.AreEqual("custom", root.GetProperty("Profile").GetString());
            Assert.AreEqual(1, root.GetProperty("Clients").GetInt32());
            Assert.AreEqual(1, root.GetProperty("Connected").GetInt32());
            Assert.AreEqual(1, root.GetProperty("HealthCheckSuccess").GetInt32());
        }
        finally
        {
            if (File.Exists(reportFile))
            {
                File.Delete(reportFile);
            }
        }
    }
}
