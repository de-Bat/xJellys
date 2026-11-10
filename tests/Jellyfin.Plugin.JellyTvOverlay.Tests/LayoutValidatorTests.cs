using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyTvOverlay.Layout;
using Xunit;

namespace Jellyfin.Plugin.JellyTvOverlay.Tests;

public class LayoutValidatorTests
{
    private const string Min = "{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\",\"text\":\"x\"}}";

    [Fact]
    public void Defaults_Filled()
    {
        var l = LayoutValidator.Validate(Min, "JellyXR");
        Assert.Equal("JellyXR/a", l.Id);
        Assert.Equal("passive", l.Focus);
        Assert.Equal(0, l.TtlMs);
        Assert.Equal(new[] { "stop", "itemStart" }, l.CloseOn);
        Assert.Empty(l.SuppressTriggers);
        Assert.Equal("JellyXR", (string?)l.Json["owner"]);
        Assert.Equal("JellyXR/a", (string?)l.Json["id"]);
        Assert.Empty(l.Json["suppressTriggers"]!.AsArray());
    }

    [Fact]
    public void WrongVersion_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":2,\"id\":\"a\",\"root\":{\"type\":\"text\"}}", "JellyXR"));
        Assert.Contains("v", ex.Message);
    }

    [Fact]
    public void BadFocus_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"focus\":\"grab\",\"root\":{\"type\":\"text\"}}", "JellyXR"));
        Assert.Contains("focus", ex.Message);
    }

    [Fact]
    public void MissingRoot_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate("{\"v\":1,\"id\":\"a\"}", "JellyXR"));
        Assert.Contains("root", ex.Message);
    }

    [Fact]
    public void UnknownNodeType_Kept()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"row\",\"children\":[{\"type\":\"sparkle\",\"glow\":3}]}}", "JellyXR");
        Assert.Equal("sparkle", (string?)l.Json["root"]!["children"]![0]!["type"]);
        Assert.Equal(3, (int?)l.Json["root"]!["children"]![0]!["glow"]);
    }

    [Fact]
    public void StepBinding_UnknownTarget_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"keys\":{\"left\":{\"op\":\"step\",\"target\":\"nope\",\"by\":-250}},\"root\":{\"type\":\"text\"}}",
            "JellyXR"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void StepBinding_KnownTarget_Ok()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"keys\":{\"left\":{\"op\":\"step\",\"target\":\"off\",\"by\":-250}},\"root\":{\"type\":\"column\",\"children\":[{\"type\":\"value\",\"id\":\"off\",\"value\":0}]}}",
            "JellyXR");
        Assert.NotNull(l.Json["keys"]!["left"]);
    }

    [Fact]
    public void ParentNamespaced()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"detail\",\"parent\":\"cast\",\"root\":{\"type\":\"text\"}}", "JellyXR");
        Assert.Equal("JellyXR/cast", l.Parent);
        Assert.Equal("JellyXR/cast", (string?)l.Json["parent"]);
    }

    [Fact]
    public void AlreadyNamespacedId_NotDoubled()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"JellyXR/a\",\"parent\":\"JellyXR/cast\",\"root\":{\"type\":\"text\"}}", "JellyXR");
        Assert.Equal("JellyXR/a", l.Id);
        Assert.Equal("JellyXR/cast", l.Parent);
    }

    [Fact]
    public void Preload_ValidatedWithSameOwner()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\"},\"preload\":[{\"v\":1,\"id\":\"b\",\"root\":{\"type\":\"text\"}}]}",
            "JellyXR");
        var p = l.Json["preload"]!.AsArray()[0]!;
        Assert.Equal("JellyXR/b", (string?)p["id"]);
        Assert.Equal("JellyXR", (string?)p["owner"]);
        Assert.Equal("passive", (string?)p["focus"]);
    }

    [Fact]
    public void Preload_Invalid_Throws()
    {
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\"},\"preload\":[{\"v\":1,\"id\":\"b\"}]}", "JellyXR"));
    }

    [Fact]
    public void SuppressTriggers_Accepted()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"suppressTriggers\":[\"pause\",\"seek\"],\"root\":{\"type\":\"text\"}}", "JellyXR");
        Assert.Equal(new[] { "pause", "seek" }, l.SuppressTriggers);
    }

    [Fact]
    public void SuppressTriggers_Unknown_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"suppressTriggers\":[\"explode\"],\"root\":{\"type\":\"text\"}}", "JellyXR"));
        Assert.Contains("suppressTriggers", ex.Message);
    }

    [Fact]
    public void CloseOn_Unknown_Throws()
    {
        var ex = Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"closeOn\":[\"stop\",\"nope\"],\"root\":{\"type\":\"text\"}}", "JellyXR"));
        Assert.Contains("closeOn", ex.Message);
    }

    [Fact]
    public void CloseOn_Custom_Used()
    {
        var l = LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"closeOn\":[\"resume\"],\"root\":{\"type\":\"text\"}}", "JellyXR");
        Assert.Equal(new[] { "resume" }, l.CloseOn);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate("{nope", "JellyXR"));
    }

    [Fact]
    public void MissingId_Throws()
    {
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"root\":{\"type\":\"text\"}}", "JellyXR"));
    }
    [Fact]
    public void DuplicateKeys_ThrowsLayoutException()
    {
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\"}}", "o"));
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate(
            "{\"v\":1,\"id\":\"a\",\"root\":{\"type\":\"text\",\"children\":[{\"id\":\"x\",\"id\":\"y\"}]}}", "o"));
    }

    [Fact]
    public void NullJson_ThrowsLayoutException()
    {
        Assert.Throws<LayoutException>(() => LayoutValidator.Validate(null!, "o"));
    }
}
