package com.xjellys.tvoverlay.model;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

import com.xjellys.tvoverlay.model.ImageSource.Kind;

public class ImageSourceTest {
    private static final String S = "https://jf.example.com:8920";
    private static final String LAN = "http://192.168.1.5:8096";

    @Test
    public void relative_server() {
        assertEquals(Kind.SERVER, ImageSource.classify("/Items/p42/Images/Primary", S));
        assertEquals(Kind.SERVER, ImageSource.classify("/Items/p42/Images/Primary", LAN));
        assertEquals("/Items/p42/Images/Primary", ImageSource.serverPath("/Items/p42/Images/Primary", S));
    }

    @Test
    public void relative_needsAllowedServer() {
        assertEquals(Kind.REJECT, ImageSource.classify("/Items/x", ""));
        assertEquals(Kind.REJECT, ImageSource.classify("/Items/x", null));
        assertEquals(Kind.REJECT, ImageSource.classify("/Items/x", "http://jf.example.com:8096"));
    }

    @Test
    public void protocolRelative_reject() {
        assertEquals(Kind.REJECT, ImageSource.classify("//evil.example.com/x.jpg", S));
        assertEquals(Kind.REJECT, ImageSource.classify("/\\evil.example.com/x.jpg", S));
    }

    @Test
    public void sameOrigin_server() {
        assertEquals(Kind.SERVER, ImageSource.classify("https://jf.example.com:8920/Items/a/Images/Primary", S));
        assertEquals(Kind.SERVER, ImageSource.classify("HTTPS://JF.Example.com:8920/Items/a", S));
        assertEquals(Kind.SERVER, ImageSource.classify("https://jf.example.com:443/x", "https://jf.example.com"));
        assertEquals(Kind.SERVER, ImageSource.classify("http://192.168.1.5:8096/Items/a", LAN));
        assertEquals("/Items/a/Images/Primary?maxWidth=300",
            ImageSource.serverPath("https://jf.example.com:8920/Items/a/Images/Primary?maxWidth=300", S));
        assertEquals("/", ImageSource.serverPath("https://jf.example.com:8920", S));
    }

    @Test
    public void sameOrigin_baseSubpathStripped() {
        String b = "https://jf.example.com/jellyfin";
        assertEquals(Kind.SERVER, ImageSource.classify("https://jf.example.com/jellyfin/Items/a/Images/Primary", b));
        assertEquals("/Items/a/Images/Primary",
            ImageSource.serverPath("https://jf.example.com/jellyfin/Items/a/Images/Primary", b));
        assertEquals("/jellyfinx/a", ImageSource.serverPath("https://jf.example.com/jellyfinx/a", b));
    }

    @Test
    public void sameHostOtherPort_external() {
        assertEquals(Kind.EXTERNAL, ImageSource.classify("https://jf.example.com/x.jpg", S));
    }

    @Test
    public void otherHttps_external() {
        assertEquals(Kind.EXTERNAL, ImageSource.classify("https://image.tmdb.org/t/p/w500/a.jpg", S));
        assertEquals(Kind.EXTERNAL, ImageSource.classify("https://image.tmdb.org/a.jpg", ""));
    }

    @Test
    public void otherHttp_reject() {
        assertEquals(Kind.REJECT, ImageSource.classify("http://image.tmdb.org/a.jpg", S));
        // a LAN http host that is not the server is still another origin
        assertEquals(Kind.REJECT, ImageSource.classify("http://192.168.1.9:8096/a.jpg", LAN));
        // http to the server host when the server is https: other origin, plain http
        assertEquals(Kind.REJECT, ImageSource.classify("http://jf.example.com:8920/a.jpg", S));
    }

    @Test
    public void fileScheme_reject() {
        assertEquals(Kind.REJECT, ImageSource.classify("file:///sdcard/a.jpg", S));
        assertEquals(Kind.REJECT, ImageSource.classify("content://media/external/images/1", S));
        assertEquals(Kind.REJECT, ImageSource.classify("javascript:alert(1)", S));
        assertEquals(Kind.REJECT, ImageSource.classify("data:image/png;base64,AAAA", S));
        assertEquals(Kind.REJECT, ImageSource.classify("Items/x", S));
    }

    @Test
    public void malformed_reject() {
        assertEquals(Kind.REJECT, ImageSource.classify(null, S));
        assertEquals(Kind.REJECT, ImageSource.classify("", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://user@jf.example.com:8920/x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://jf.example.com:8920@evil.com/x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://evil.com\\@jf.example.com:8920/x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https:///x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://a.com:99999/x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://a.com:8x/x", S));
        assertEquals(Kind.REJECT, ImageSource.classify("/Items/a b", S));
        assertEquals(Kind.REJECT, ImageSource.classify("https://a.com/x\n", S));
    }
}
