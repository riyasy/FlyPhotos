using FlyPhotos.Infra.Configuration;
using FlyPhotos.UI.Views;
using Windows.System;

namespace FlyPhotos.Tests;

/// <summary>The routing table the photo window builds from the default keys and the user's KeyBindings overrides.</summary>
[TestClass]
public sealed class KeyBindingTests
{
    [TestInitialize]
    public void Setup() => AppConfig.Settings = new AppSettings();

    private static KeyChord K(VirtualKey key, bool ctrl = false) => new(key, ctrl, false, false);

    private static void Bind(CommandId id, params KeyChord[] chords) =>
        AppConfig.Settings.KeyBindings[id.ToString()] = chords.Select(c => c.Format()).ToList();

    [TestMethod]
    public void Defaults_RouteNavigationAndZoom()
    {
        var routes = ShortcutCatalog.Resolve();
        Assert.AreEqual(CommandId.NextPhoto, routes[K(VirtualKey.Right)]);
        Assert.AreEqual(CommandId.PrevPhoto, routes[K(VirtualKey.Left)]);
        Assert.AreEqual(CommandId.FirstPhoto, routes[K(VirtualKey.Home)]);
        Assert.AreEqual(CommandId.LastPhoto, routes[K(VirtualKey.End)]);
        Assert.AreEqual(CommandId.ZoomIn, routes[K(VirtualKey.Up)]);
        Assert.AreEqual(CommandId.PanRight, routes[K(VirtualKey.Right, ctrl: true)]);
        Assert.AreEqual(CommandId.NextFolder, routes[K(VirtualKey.PageDown, ctrl: true)]);
    }

    [TestMethod]
    public void AnOverride_ReplacesTheDefaultKeys()
    {
        Bind(CommandId.NextPhoto, K(VirtualKey.N), K(VirtualKey.Space));
        var routes = ShortcutCatalog.Resolve();
        Assert.AreEqual(CommandId.NextPhoto, routes[K(VirtualKey.N)]);
        Assert.AreEqual(CommandId.NextPhoto, routes[K(VirtualKey.Space)]);
        Assert.IsFalse(routes.ContainsKey(K(VirtualKey.Right)), "the default key is freed");
        Assert.AreEqual(CommandId.PrevPhoto, routes[K(VirtualKey.Left)], "other commands keep theirs");
    }

    [TestMethod]
    public void AnEmptyList_Unbinds()
    {
        Bind(CommandId.PrevPhoto);
        Assert.IsFalse(ShortcutCatalog.Resolve().ContainsValue(CommandId.PrevPhoto));
    }

    [TestMethod]
    public void AnUnreadableToken_IsDropped_TheRestKept()
    {
        AppConfig.Settings.KeyBindings[CommandId.NextPhoto.ToString()] = ["no such key", K(VirtualKey.N).Format()];
        var routes = ShortcutCatalog.Resolve();
        Assert.AreEqual(CommandId.NextPhoto, routes[K(VirtualKey.N)]);
        Assert.AreEqual(1, routes.Count(r => r.Value == CommandId.NextPhoto));
    }

    [TestMethod]
    public void AReservedCommand_CannotBeRebound()
    {
        Bind(CommandId.DeletePhoto, K(VirtualKey.X));
        var routes = ShortcutCatalog.Resolve();
        Assert.AreEqual(CommandId.DeletePhoto, routes[K(VirtualKey.Delete)]);
        Assert.IsFalse(routes.ContainsKey(K(VirtualKey.X)));
    }

    [TestMethod]
    public void NullBindings_AreTreatedAsNone()
    {
        AppConfig.Settings.KeyBindings = null!;
        AppConfig.Settings.KeyBindings[CommandId.NextPhoto.ToString()] = null!;
        Assert.AreEqual(CommandId.NextPhoto, ShortcutCatalog.Resolve()[K(VirtualKey.Right)]);
    }

    [TestMethod]
    public void EveryDefaultChord_SurvivesASaveAndReload()
    {
        foreach (var chord in ShortcutCatalog.Resolve().Keys)
            Assert.IsTrue(KeyChord.TryParse(chord.Format(), out var back) && back == chord, chord.Format());
    }
}
