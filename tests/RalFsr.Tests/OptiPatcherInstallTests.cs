using RalFsrHelper.Core;
using Xunit;

namespace RalFsrHelper.Tests;

[CollectionDefinition("OptiPatcher", DisableParallelization = true)]
public class OptiPatcherCollection { }

[Collection("OptiPatcher")]
public class OptiPatcherInstallTests : TempFolderTest
{
    private readonly string _game;
    private readonly OptiPatcherFile _v42;
    private readonly OptiPatcherFile _v43;

    public OptiPatcherInstallTests()
    {
        _game = Path.Combine(Root, "Game");
        _v42 = Asi("v0.42", "a");
        _v43 = Asi("v0.43", "b");
    }

    private OptiPatcherFile Asi(string tag, string marker)
    {
        var path = Path.Combine(Root, $"{tag}.asi");
        File.WriteAllBytes(path, Dll(marker));
        return new OptiPatcherFile(tag, path);
    }

    /// <summary>A game set up the way Installer.Install leaves it (v10 layout: an OptiScaler folder next to the exe).</summary>
    private void SetUpGame(bool v10Layout = true)
    {
        Directory.CreateDirectory(_game);
        File.WriteAllText(Path.Combine(_game, "game.exe"), "exe");
        var m = new InstallManifest { GameName = "DOOM Eternal", ProxyName = "dxgi.dll", ExeName = "DOOMEternalx64vk.exe" };
        File.WriteAllText(Path.Combine(_game, "dxgi.dll"), "opti");
        m.CreatedFiles.Add("dxgi.dll");
        File.WriteAllText(Path.Combine(_game, "OptiScaler.ini"), "[Plugins]\nLoadAsiPlugins=auto\n");
        m.CreatedFiles.Add("OptiScaler.ini");
        if (v10Layout)
        {
            Directory.CreateDirectory(Path.Combine(_game, "OptiScaler"));
            File.WriteAllText(Path.Combine(_game, "OptiScaler", "amdxcffx64.dll"), "fsr4");
            m.CreatedDirs.Add("OptiScaler");
            m.CreatedFiles.Add(Path.Combine("OptiScaler", "amdxcffx64.dll"));
        }
        Directory.CreateDirectory(InstallManifest.MetaDir(_game));
        m.Save(_game);
    }

    private string Ini => File.ReadAllText(Path.Combine(_game, "OptiScaler.ini"));
    private string Placed => Path.Combine(_game, "OptiScaler", "plugins", "OptiPatcher.asi");

    [Fact]
    public void Added_in_optiscalers_plugins_folder_with_asi_loading_on_and_removed_at_uninstall()
    {
        SetUpGame();
        Assert.True(Installer.AddOptiPatcher(_game, _v42));

        Assert.Equal(File.ReadAllBytes(_v42.AsiPath), File.ReadAllBytes(Placed));
        Assert.Contains("LoadAsiPlugins=true", Ini);
        var m = InstallManifest.TryLoad(_game)!;
        Assert.Equal("v0.42", m.OptiPatcherVersion);
        Assert.Contains(Path.Combine("OptiScaler", "plugins", "OptiPatcher.asi"), m.CreatedFiles);

        Installer.Uninstall(_game);
        Assert.False(Directory.Exists(Path.Combine(_game, "OptiScaler")));
        Assert.False(File.Exists(Path.Combine(_game, "OptiScaler.ini")));
        Assert.Equal(new[] { "game.exe" }, Directory.GetFileSystemEntries(_game).Select(Path.GetFileName));
    }

    [Fact]
    public void Older_installs_without_an_optiscaler_folder_use_plugins_next_to_the_exe()
    {
        SetUpGame(v10Layout: false);
        Installer.AddOptiPatcher(_game, _v42);
        Assert.True(File.Exists(Path.Combine(_game, "plugins", "OptiPatcher.asi")));

        Installer.Uninstall(_game);
        Assert.False(Directory.Exists(Path.Combine(_game, "plugins")));
    }

    [Fact]
    public void A_players_own_plugins_stay_at_uninstall()
    {
        SetUpGame();
        Installer.AddOptiPatcher(_game, _v42);
        var mine = Path.Combine(_game, "OptiScaler", "plugins", "MyMod.asi");
        File.WriteAllText(mine, "mine");

        Installer.Uninstall(_game);
        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(Placed));
        Assert.False(File.Exists(Path.Combine(_game, "OptiScaler", "amdxcffx64.dll")));
    }

    [Fact]
    public void A_plugins_folder_the_player_already_had_stays_even_when_empty()
    {
        SetUpGame(v10Layout: false);
        Directory.CreateDirectory(Path.Combine(_game, "plugins"));
        Installer.AddOptiPatcher(_game, _v42);

        Installer.Uninstall(_game);
        Assert.True(Directory.Exists(Path.Combine(_game, "plugins")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_game, "plugins")));
    }

    [Fact]
    public void A_players_own_optipatcher_is_left_as_is()
    {
        SetUpGame();
        Directory.CreateDirectory(Path.GetDirectoryName(Placed)!);
        File.WriteAllText(Placed, "players own copy");

        Assert.False(Installer.AddOptiPatcher(_game, _v42));
        Assert.Equal("players own copy", File.ReadAllText(Placed));
        Assert.Contains("LoadAsiPlugins=true", Ini);

        Installer.Uninstall(_game);
        Assert.Equal("players own copy", File.ReadAllText(Placed));
    }

    [Fact]
    public void Repair_puts_it_back_and_a_newer_release_replaces_it()
    {
        SetUpGame();
        Installer.AddOptiPatcher(_game, _v42);
        Assert.False(Installer.AddOptiPatcher(_game, _v42)); // already current

        File.Delete(Placed); // a game update or file check removed it
        Assert.True(Installer.AddOptiPatcher(_game, _v42));
        Assert.True(File.Exists(Placed));

        Assert.True(Installer.AddOptiPatcher(_game, _v43));
        Assert.Equal(File.ReadAllBytes(_v43.AsiPath), File.ReadAllBytes(Placed));
        Assert.Equal("v0.43", InstallManifest.TryLoad(_game)!.OptiPatcherVersion);
        Assert.Single(InstallManifest.TryLoad(_game)!.CreatedFiles, f => f.EndsWith("OptiPatcher.asi"));
    }

    [Fact]
    public void LoadAsiPlugins_is_turned_back_on_when_optiscaler_wrote_it_off()
    {
        SetUpGame();
        Installer.AddOptiPatcher(_game, _v42);
        File.WriteAllText(Path.Combine(_game, "OptiScaler.ini"), "[Plugins]\nLoadAsiPlugins=false\n");
        Installer.AddOptiPatcher(_game, _v42);
        Assert.Contains("LoadAsiPlugins=true", Ini);
    }

    [Fact]
    public void An_optiscaler_update_keeps_it()
    {
        SetUpGame();
        Installer.AddOptiPatcher(_game, _v42);
        var package = Path.Combine(Root, "OptiScaler_new");
        Directory.CreateDirectory(Path.Combine(package, "OptiScaler"));
        File.WriteAllText(Path.Combine(package, "OptiScaler.dll"), "new opti");
        File.WriteAllText(Path.Combine(package, "OptiScaler.ini"), "[Plugins]\nLoadAsiPlugins=auto\n");
        File.WriteAllText(Path.Combine(package, "OptiScaler", "libxess.dll"), "x");

        Assert.True(Installer.UpdateOpti(_game, new OptiPackage("new", package)));
        Assert.True(File.Exists(Placed));
        Assert.Contains("LoadAsiPlugins=true", Ini);
        Assert.Equal("v0.42", InstallManifest.TryLoad(_game)!.OptiPatcherVersion);
    }

    [Fact]
    public void Not_for_a_game_that_isnt_set_up()
    {
        Directory.CreateDirectory(_game);
        Assert.Throws<InvalidOperationException>(() => Installer.AddOptiPatcher(_game, _v42));
        Assert.False(Directory.Exists(Path.Combine(_game, "plugins")));
    }
}

public class CompatListOptiPatcherTests
{
    private static readonly List<CompatEntry> Entries =
        CompatList.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "Compatibility-List.md")));

    private static CompatEntry Named(string name) => Entries.Single(e => CompatList.Key(e.Name) == CompatList.Key(name));

    [Fact]
    public void Doom_eternal_is_marked_for_optipatcher() => Assert.True(Named("DOOM Eternal").OptiPatcher);

    [Fact]
    public void Horizon_zero_dawn_complete_edition_is_not() =>
        Assert.False(Named("Horizon Zero Dawn Complete Edition").OptiPatcher);

    [Fact]
    public void Many_games_are_marked() => Assert.InRange(Entries.Count(e => e.OptiPatcher), 150, 1000);
}

public class GamePassNameTests
{
    private static readonly List<CompatEntry> Entries =
        CompatList.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "Compatibility-List.md")));

    /// <summary>CompatList.Find's loose match, on the bundled list.</summary>
    private static CompatEntry? Loose(string storeName) =>
        Entries.FirstOrDefault(e => CompatList.Key(e.Name) == CompatList.Key(storeName))
        ?? Entries.FirstOrDefault(e => CompatList.BaseKey(e.Name) == CompatList.BaseKey(storeName));

    [Theory]
    [InlineData("Doom Eternal - PC")]
    [InlineData("DOOM Eternal (PC)")]
    [InlineData("DOOM Eternal for Windows 10")]
    [InlineData("DOOM Eternal Windows Edition")]
    public void Game_pass_platform_tags_still_find_the_game(string storeName)
    {
        var entry = Loose(storeName);
        Assert.NotNull(entry);
        Assert.Equal("doometernal", CompatList.Key(entry!.Name));
        Assert.True(entry.OptiPatcher);
    }

    [Theory]
    [InlineData("PC Building Simulator", "pcbuildingsimulator")]
    [InlineData("Windows Defender Test", "windowsdefendertest")]
    [InlineData("Arcade Spirits PCX", "arcadespiritspcx")]
    public void Names_that_only_contain_pc_or_windows_are_untouched(string name, string key) =>
        Assert.Equal(key, CompatList.BaseKey(name));

    [Theory]
    [InlineData("DLSS", "DLSS")]
    [InlineData("DLSS,FSR 3.1", "FSR")]
    [InlineData("DLSS,XeSS", "DLSS")]
    [InlineData("XeSS", "XeSS")]
    [InlineData("", "FSR")]
    public void The_card_names_an_upscaler_the_game_has(string found, string pick) =>
        Assert.Equal(pick, GameScan.UpscalerToPick(found.Split(',', StringSplitOptions.RemoveEmptyEntries)));
}
