using RalFsrHelper.Core;
using Xunit;

namespace RalFsrHelper.Tests;

/// <summary>Folders FSR 4.1 Lite never installs into, from the Nov 2026 Assetto Corsa / No Hesi report.</summary>
public class InstallFolderTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void The_touch_keyboards_folder_is_refused()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        Assert.True(InstallFolders.IsUnsafe(Path.Combine(common, "microsoft shared", "ink")));
        Assert.True(InstallFolders.IsUnsafe(common));
    }

    [Fact]
    public void Windows_and_the_top_of_program_files_and_drives_are_refused()
    {
        Assert.True(InstallFolders.IsUnsafe(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(InstallFolders.IsUnsafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32")));
        Assert.True(InstallFolders.IsUnsafe(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        Assert.True(InstallFolders.IsUnsafe(@"C:\"));
        Assert.True(InstallFolders.IsUnsafe(""));
    }

    [Fact]
    public void The_users_own_folders_are_refused_with_or_without_a_trailing_slash()
    {
        Assert.True(InstallFolders.IsUnsafe(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)));
        Assert.True(InstallFolders.IsUnsafe(Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + @"\"));
        Assert.True(InstallFolders.IsUnsafe(Path.Combine(Home, "Downloads")));
        Assert.True(InstallFolders.IsUnsafe(Path.Combine(Home, "Documents")));
        Assert.True(InstallFolders.IsUnsafe(Home));
    }

    [Fact]
    public void Real_game_folders_are_fine_even_under_the_users_folders()
    {
        Assert.False(InstallFolders.IsUnsafe(@"C:\Program Files (x86)\Steam\steamapps\common\assettocorsa"));
        Assert.False(InstallFolders.IsUnsafe(@"C:\Program Files\WindowsApps\BethesdaSoftworks.DOOMEternal-PC_1.0.56.0_x64__3275kfvn8vcwc"));
        Assert.False(InstallFolders.IsUnsafe(@"C:\Games\Fight Night Champion"));
        Assert.False(InstallFolders.IsUnsafe(@"D:\XboxGames\Forza Horizon 6\Content"));
        Assert.False(InstallFolders.IsUnsafe(Path.Combine(Home, "Downloads", "SomeGame")));
        Assert.False(InstallFolders.IsUnsafe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Portable Game")));
    }
}

public class NothingToUpgradeTests
{
    [Fact]
    public void A_dx11_game_with_no_upscaler_and_not_on_the_list_has_nothing_to_upgrade()
    {
        Assert.True(GameScan.NothingToUpgrade(GameState.NoUpscalerFound, listLoaded: true, onList: false, api: "DX11"));
    }

    [Theory]
    [InlineData(GameState.NoUpscalerFound, true, true, "DX11")]   // on the list: the community says it works
    [InlineData(GameState.NoUpscalerFound, true, false, "DX12")]  // DX12 games often build FSR 2 or 3 in
    [InlineData(GameState.NoUpscalerFound, true, false, "Vulkan")]
    [InlineData(GameState.NoUpscalerFound, true, false, null)]    // API not known yet
    [InlineData(GameState.NoUpscalerFound, false, false, "DX11")] // list couldn't be read (offline)
    [InlineData(GameState.Ready, true, false, "DX11")]            // has an upscaler
    [InlineData(GameState.Installed, true, false, "DX11")]        // already installed: uninstall stays available
    public void Everything_else_stays_installable(GameState state, bool listLoaded, bool onList, string? api)
    {
        Assert.False(GameScan.NothingToUpgrade(state, listLoaded, onList, api));
    }

    [Fact]
    public void Assetto_corsa_isnt_on_optiscalers_list_but_its_sequels_are()
    {
        var entries = CompatList.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "Compatibility-List.md")));
        var keys = entries.Select(e => CompatList.Key(e.Name)).ToHashSet();
        Assert.DoesNotContain(CompatList.Key("Assetto Corsa"), keys);
        Assert.Contains(CompatList.Key("Assetto Corsa Competizione"), keys);
    }

    [Fact]
    public void The_card_line_is_one_plain_sentence()
    {
        Assert.DoesNotContain("—", GameScan.NothingToUpgradeText);
        Assert.EndsWith(".", GameScan.NothingToUpgradeText);
    }
}
