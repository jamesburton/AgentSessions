using System.Text;
using AgentSessions.Cli;

namespace AgentSessions.Tests.Cli;

public class ConsoleSetupTests
{
    [Fact]
    public void Not_redirected_sets_utf8_without_bom_for_output_and_input()
    {
        Encoding? output = null, input = null;

        var ok = ConsoleSetup.UseUtf8(outputRedirected: false, e => output = e, e => input = e);

        Assert.True(ok);
        Assert.Equal(65001, output!.CodePage);
        Assert.Empty(output.GetPreamble());
        Assert.Equal(65001, input!.CodePage);
        Assert.Empty(input.GetPreamble());
    }

    [Fact]
    public void Redirected_output_leaves_the_encodings_alone()
    {
        var calls = 0;
        var ok = ConsoleSetup.UseUtf8(outputRedirected: true, _ => calls++, _ => calls++);
        Assert.False(ok);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_console_that_refuses_the_encoding_never_crashes()
    {
        Encoding? input = null;
        var ok = ConsoleSetup.UseUtf8(false, _ => throw new IOException("The handle is invalid."), e => input = e);
        Assert.False(ok);
        Assert.NotNull(input);   // a refused output encoding does not stop the input one

        Assert.False(ConsoleSetup.UseUtf8(false, _ => { }, _ => throw new PlatformNotSupportedException()));
    }
}
