package com.xjellys.tvoverlay.model;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

public class ServerUrlTest {
    @Test
    public void bareHost_https8920() {
        assertEquals("https://jf.example.com:8920", ServerUrl.normalize("jf.example.com"));
        assertEquals("https://jf.example.com:8920", ServerUrl.normalize("  jf.example.com/ "));
    }

    @Test
    public void bareHostWithPort_keepsPort() {
        assertEquals("https://192.168.1.10:8096", ServerUrl.normalize("192.168.1.10:8096"));
    }

    @Test
    public void explicitHttp_kept() {
        assertEquals("http://192.168.1.5:8096", ServerUrl.normalize("http://192.168.1.5:8096/"));
        assertEquals("https://jf.example.com", ServerUrl.normalize("https://jf.example.com"));
    }

    @Test
    public void emptyStaysEmpty() {
        assertEquals("", ServerUrl.normalize("  "));
        assertEquals("", ServerUrl.normalize(null));
    }

    @Test
    public void isPrivate_true() {
        String[] yes = {"192.168.1.5", "10.0.0.2", "172.16.4.1", "172.31.255.1", "169.254.1.1", "localhost",
            "nas.local", "fd00::1", "[fd00::1]", "fe80::1", "127.0.0.1"};
        for (String h : yes) assertTrue(ServerUrl.isPrivate(h), h);
    }

    @Test
    public void isPrivate_false() {
        String[] no = {"172.32.0.1", "172.15.0.1", "8.8.8.8", "jf.example.com", "192.168.1.5.evil.com",
            "10.0.0.2.example.com", "2001:db8::1", "", null};
        for (String h : no) assertFalse(ServerUrl.isPrivate(h), String.valueOf(h));
    }

    @Test
    public void allowed() {
        assertFalse(ServerUrl.allowed("http://8.8.8.8:8096"));
        assertTrue(ServerUrl.allowed("http://192.168.1.5:8096"));
        assertTrue(ServerUrl.allowed("https://jf.example.com"));
        assertFalse(ServerUrl.allowed("http://jf.example.com:8096"));
        assertTrue(ServerUrl.allowed("http://[fd00::1]:8096"));
        assertFalse(ServerUrl.allowed("ftp://192.168.1.5"));
        assertFalse(ServerUrl.allowed("http://192.168.1.5@evil.com/"));
        assertFalse(ServerUrl.allowed(""));
    }

    @Test
    public void httpFallback_onlyForPrivateHosts() {
        assertEquals("http://192.168.1.10:8096", ServerUrl.httpFallback("https://192.168.1.10:8920"));
        assertEquals("http://nas.local:8096", ServerUrl.httpFallback("https://nas.local:8920"));
        assertNull(ServerUrl.httpFallback("https://jf.example.com:8920"));
        assertNull(ServerUrl.httpFallback("http://192.168.1.10:8096"));
    }

    @Test
    public void tricky_hosts_not_private() {
        String[] no = {"::ffff:8.8.8.8", "localhost.evil.com", "evil.local.com", "010.1.1.1", "172.017.0.1",
            "192.168.01.5", "10.0.0.02", "192.168.1"};
        for (String h : no) assertFalse(ServerUrl.isPrivate(h), h);
    }

    @Test
    public void tricky_urls() {
        assertFalse(ServerUrl.allowed("HTTP://8.8.8.8"));
        assertTrue(ServerUrl.allowed("HTTP://NAS.LOCAL:8096"));
        assertTrue(ServerUrl.allowed("http://192.168.1.5:8096"));
        assertTrue(ServerUrl.allowed("HTTPS://jf.example.com"));
        assertFalse(ServerUrl.allowed("https://user@host"));
        assertFalse(ServerUrl.allowed("http://192.168.1.5@evil.com"));
        assertTrue(ServerUrl.allowed("http://[fd00::1]:8096"));
        assertFalse(ServerUrl.allowed("http://[2001:db8::1]:8096"));
    }

    @Test
    public void normalize_bareIpv6() {
        assertEquals("https://[fd00::1]:8920", ServerUrl.normalize("fd00::1"));
        assertEquals("https://[fd00::1]:8096", ServerUrl.normalize("[fd00::1]:8096"));
    }
}
