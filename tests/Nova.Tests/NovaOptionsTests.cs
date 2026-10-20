using Nova.Server.Services;

namespace Nova.Tests;

public class NovaOptionsTests
{
    [Fact]
    public void TheSettingsFolderIsAlwaysAFullPath()
    {
        // Never a bare "nova" (relative to wherever the service runs, e.g. /opt/nova, where nothing can be saved),
        // even before ~/.config exists.
        var folder = NovaOptions.DefaultConfigDirectory();
        Assert.True(Path.IsPathRooted(folder), folder);
        Assert.Equal("nova", Path.GetFileName(folder));
        Assert.Equal(folder, new NovaOptions().ResolveConfigDirectory());
        Assert.Equal("/somewhere/else", new NovaOptions { ConfigDirectory = "/somewhere/else" }.ResolveConfigDirectory());
        Assert.True(Path.IsPathRooted(new NovaOptions().ResolveImageDirectory()));
    }
}
