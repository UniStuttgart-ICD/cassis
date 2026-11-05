using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cassis;
using Cassis.Routing;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class InstanceMatchTests
{
    [Test]
    public void MissingHeader_DoesNotPickARhino()
    {
        var result = InstanceMatcher.Match(null, new[] { OneFile(1, 10, @"C:\proj\a.gh", @"C:\proj\a.gh") });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.MissingHeader));
        Assert.That(result.DocumentPath, Is.Null);
    }

    [Test]
    public void UnexpandedHeader_DoesNotPickARhino()
    {
        var result = InstanceMatcher.Match("${workspaceFolder}", Array.Empty<InstanceSnapshot>());

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.UnexpandedHeader));
        Assert.That(result.Detail, Does.Contain("${workspaceFolder}"));
    }

    [Test]
    public void NoFileInsideFolder_NamesTheFolder()
    {
        var result = InstanceMatcher.Match(@"C:\proj", new[] { OneFile(1, 10, @"D:\other\a.gh", @"D:\other\a.gh") });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.NoFile));
        Assert.That(result.Detail, Does.Contain(@"C:\proj"));
    }

    [Test]
    public void OneFile_MatchesSlashAndCase()
    {
        var result = InstanceMatcher.Match(@"c:/proj", new[] { OneFile(4, 40, @"C:\proj\Model.gh", @"C:\proj\Model.gh") });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.Ok));
        Assert.That(result.Pid, Is.EqualTo(4));
        Assert.That(result.Port, Is.EqualTo(40));
        Assert.That(result.DocumentPath, Is.EqualTo(@"C:\proj\Model.gh"));
    }

    [Test]
    public void TwoRhinos_DoesNotGuess()
    {
        var result = InstanceMatcher.Match(
            @"C:\proj",
            new[]
            {
                OneFile(1, 10, @"C:\proj\a.gh", @"C:\proj\a.gh"),
                OneFile(2, 11, @"C:\proj\b.gh", @"C:\proj\b.gh"),
            });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.TwoRhinos));
        Assert.That(result.Detail, Does.Contain("1"));
        Assert.That(result.Detail, Does.Contain("2"));
    }

    [Test]
    public void TwoFilesInOneRhino_UsesTheActiveFile()
    {
        var result = InstanceMatcher.Match(@"C:\proj", new[] { TwoFiles(7, 70, @"C:\proj\active.gh", @"C:\proj\other.gh") });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.Ok));
        Assert.That(result.DocumentPath, Is.EqualTo(@"C:\proj\active.gh"));
    }

    [Test]
    public void TwoFilesInOneRhino_WithoutActiveFile_DoesNotGuess()
    {
        var snapshot = TwoFiles(7, 70, @"C:\proj\a.gh", @"C:\proj\b.gh");
        snapshot.ActivePath = null;
        var result = InstanceMatcher.Match(@"C:\proj", new[] { snapshot });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.TwoFiles));
        Assert.That(result.Detail, Does.Contain(@"C:\proj\a.gh"));
        Assert.That(result.Detail, Does.Contain(@"C:\proj\b.gh"));
    }

    [Test]
    public void WorkerDown_NamesRestart_NotAMissingFile()
    {
        var result = InstanceMatcher.Match(@"C:\proj", new[] { OneFile(7, 0, @"C:\proj\a.gh", @"C:\proj\a.gh") });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.WorkerDown));
        Assert.That(result.Detail, Does.Contain("Restart Server"));
        Assert.That(result.Detail, Does.Not.Contain("Save the file"));
    }

    [Test]
    public void DownWorker_DoesNotBlockTheLiveRhino()
    {
        var result = InstanceMatcher.Match(
            @"C:\proj",
            new[]
            {
                OneFile(1, 0, @"C:\proj\a.gh", @"C:\proj\a.gh"),
                OneFile(2, 11, @"C:\proj\b.gh", @"C:\proj\b.gh"),
            });

        Assert.That(result.Kind, Is.EqualTo(InstanceMatchKind.Ok));
        Assert.That(result.Pid, Is.EqualTo(2));
    }

    [Test]
    public void ErrorJson_RoundTripsAsJsonRpcMessage()
    {
        var request = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "tools/call",
        };

        var json = WorkerCall.ErrorJson(request, "No open Grasshopper file is inside C:\\proj.");
        var message = JsonSerializer.Deserialize<JsonRpcMessage>(json);

        Assert.That(message, Is.InstanceOf<JsonRpcError>());
        Assert.That(((JsonRpcError)message!).Error.Message, Does.Contain("C:\\proj"));
    }

    [Test]
    public void ToolRequest_RoundTripsAsJsonRpcRequest()
    {
        var request = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/call",
            Params = JsonNode.Parse("{\"name\":\"Get_ComponentCount\",\"arguments\":{}}"),
        };

        var json = JsonSerializer.Serialize(request);
        var message = JsonSerializer.Deserialize<JsonRpcMessage>(json);

        Assert.That(message, Is.InstanceOf<JsonRpcRequest>());
        var revived = (JsonRpcRequest)message!;
        Assert.That(revived.Method, Is.EqualTo("tools/call"));
        Assert.That(revived.Params?["name"]?.GetValue<string>(), Is.EqualTo("Get_ComponentCount"));
    }

    [Test]
    public void ReadLive_SkipsDeadAndStaleRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cassis-instances-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.UtcNow;
            InstanceFiles.Write(directory, new InstanceSnapshot
            {
                Pid = 11,
                Port = 1100,
                Paths = new List<string> { @"C:\proj\a.gh" },
                Utc = now,
            });
            InstanceFiles.Write(directory, new InstanceSnapshot
            {
                Pid = 12,
                Port = 1200,
                Paths = new List<string> { @"C:\proj\b.gh" },
                Utc = now.AddMinutes(-5),
            });

            var live = InstanceFiles.ReadLive(directory, now, pid => pid == 11, TimeSpan.FromSeconds(20));

            Assert.That(live.Select(item => item.Pid), Is.EqualTo(new[] { 11 }));
            Assert.That(File.Exists(Path.Combine(directory, "instances", "12.json")), Is.False);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void ReadLive_KeepsAFreshStoppedWorker()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cassis-instances-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.UtcNow;
            InstanceFiles.Write(directory, new InstanceSnapshot
            {
                Pid = 11,
                Port = 0,
                Paths = new List<string> { @"C:\proj\a.gh" },
                Utc = now,
            });

            var live = InstanceFiles.ReadLive(directory, now, _ => true, TimeSpan.FromSeconds(20));

            Assert.That(live.Select(item => item.Pid), Is.EqualTo(new[] { 11 }));
            Assert.That(live[0].Port, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ForwardAsync_KeepsAWorkerErrorBody()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        try
        {
            var served = listener.GetContextAsync().ContinueWith(task =>
            {
                var context = task.Result;
                var bytes = Encoding.UTF8.GetBytes(
                    "{\"jsonrpc\":\"2.0\",\"id\":4,\"error\":{\"code\":-32603,\"message\":\"focus failed\"}}");
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            });

            var request = new JsonRpcRequest
            {
                Id = new RequestId(4),
                Method = "tools/call",
            };
            var body = await WorkerCall.ForwardAsync(port, @"C:\proj\a.gh", request, true, CancellationToken.None);

            await served;
            Assert.That(body, Does.Contain("focus failed"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public void Lock_YieldsWhenTheOwnerIsDead()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cassis-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.That(FrontDoorLock.TryAcquire(directory, 1, _ => true), Is.True);
            Assert.That(FrontDoorLock.TryAcquire(directory, 2, pid => pid == 1), Is.False);
            Assert.That(FrontDoorLock.TryAcquire(directory, 2, _ => false), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(directory, "front-door.lock")).Trim(), Is.EqualTo("2"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static InstanceSnapshot OneFile(int pid, int port, string active, string path)
    {
        return new InstanceSnapshot
        {
            Pid = pid,
            Port = port,
            ActivePath = active,
            Paths = new List<string> { path },
            Utc = DateTime.UtcNow,
        };
    }

    private static InstanceSnapshot TwoFiles(int pid, int port, string active, string other)
    {
        return new InstanceSnapshot
        {
            Pid = pid,
            Port = port,
            ActivePath = active,
            Paths = new List<string> { active, other },
            Utc = DateTime.UtcNow,
        };
    }
}

[TestFixture]
public class StartDecisionTests
{
    [Test]
    public void ErrorWithListenerStillUp_Recovers()
    {
        Assert.That(CassisRuntime.DecideStart(true, "Error"), Is.EqualTo(CassisRuntime.StartDecision.Recover));
        Assert.That(CassisRuntime.DecideStart(true, "Error (report written)"), Is.EqualTo(CassisRuntime.StartDecision.Recover));
    }

    [Test]
    public void ErrorWithNoListener_Starts()
    {
        Assert.That(CassisRuntime.DecideStart(false, "Error"), Is.EqualTo(CassisRuntime.StartDecision.Start));
    }

    [Test]
    public void LiveWorker_DoesNotStartAgain()
    {
        Assert.That(CassisRuntime.DecideStart(true, "Running"), Is.EqualTo(CassisRuntime.StartDecision.Ignore));
        Assert.That(CassisRuntime.DecideStart(true, "Stopped"), Is.EqualTo(CassisRuntime.StartDecision.Ignore));
    }

    [Test]
    public void StoppedWithNoListener_Starts()
    {
        Assert.That(CassisRuntime.DecideStart(false, "Stopped"), Is.EqualTo(CassisRuntime.StartDecision.Start));
    }
}
