using AdaPanel.Infrastructure.Mail;
using AdaPanel.Infrastructure.Support;
using Xunit;

namespace AdaPanel.Infrastructure.Tests;

public sealed class MailQueryMeasurementTests
{
    [Theory]
    [InlineData("broken json")]
    [InlineData("{\"id\":\"1\"}\n{truncated")]
    [InlineData("{\"id\":\"1\"}\nwarning")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("{\"items\":null}")]
    public void Invalid_or_partial_inventory_is_not_a_successful_empty_query(string output)
    {
        var result = MailService.MailQueryResult.Olc(new CommandResult { ExitCode = 0, StandardOutput = output });
        Assert.False(result.Measured);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    [InlineData("{\"id\":\"1\"}\n{\"id\":\"2\"}", 2)]
    [InlineData("{\n  \"id\": \"1\"\n}", 1)]
    [InlineData("{\"items\":[{\"id\":\"1\"}]}", 1)]
    [InlineData("[{\"id\":\"1\"}]", 1)]
    public void Valid_cli_shapes_remain_measurable(string output, int count)
    {
        var result = MailService.MailQueryResult.Olc(new CommandResult { ExitCode = 0, StandardOutput = output });
        Assert.True(result.Measured);
        Assert.Equal(count, result.Items.Count);
    }
}
