using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ModelContextProtocol.HttpListener.Tests;

[McpServerToolType]
public static class FixtureTools
{
    [McpServerTool(Name = "echo")]
    [Description("Echoes the given text")]
    public static Task<CallToolResult> Echo([Description("Text to echo")] string text) =>
        Task.FromResult(new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = text } }
        });

    [McpServerTool(Name = "richresult")]
    [Description("Returns an error result")]
    public static Task<CallToolResult> RichResult() =>
        Task.FromResult(new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = "rich failure" } }
        });

    [McpServerTool(Name = "throws")]
    [Description("Always throws")]
    public static Task<CallToolResult> Throws() =>
        throw new InvalidOperationException("fixture boom");
}

[McpServerPromptType]
public static class FixturePrompts
{
    [McpServerPrompt(Name = "greet")]
    [Description("Greets a person")]
    public static FixtureChatMessage Greet([Description("Name to greet")] string name) =>
        new() { Role = "user", Text = $"Hello {name}" };
}

public sealed class FixtureChatMessage
{
    public string? Role { get; init; }

    public string? Text { get; init; }
}
