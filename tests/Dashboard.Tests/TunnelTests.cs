using Dashboard.Api;
using Xunit;

namespace Dashboard.Tests;

public class TunnelTests
{
    [Fact] public void InspectorUrlCannotReplaceDashboardUrl()
    {
        Assert.Equal("https://abc-4317.jpe1.devtunnels.ms", TunnelWorker.BrowserUrl("Connect via browser: https://abc-4317.jpe1.devtunnels.ms", 4317));
        Assert.Null(TunnelWorker.BrowserUrl("Inspect network activity: https://abc-4317-inspect.jpe1.devtunnels.ms", 4317));
        Assert.Null(TunnelWorker.BrowserUrl("Connect via browser: https://abc-4319.jpe1.devtunnels.ms", 4317));
    }
}
