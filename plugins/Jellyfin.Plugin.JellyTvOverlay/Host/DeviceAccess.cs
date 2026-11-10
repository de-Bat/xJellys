namespace Jellyfin.Plugin.JellyTvOverlay.Host;

/// <summary>Who may use a device's overlay endpoints: a user with a session on that device; admins: any device with a session. Pure.</summary>
public static class DeviceAccess
{
    public static bool CanUse(Guid userId, bool isAdmin, string deviceId, IEnumerable<(string DeviceId, Guid UserId)> sessions)
    {
        if (string.IsNullOrEmpty(deviceId) || (!isAdmin && userId == Guid.Empty))
        {
            return false;
        }

        // Admins too need a live session on the device, so unknown ids never create per-device state.
        return sessions.Any(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal) && (isAdmin || s.UserId == userId));
    }
}
