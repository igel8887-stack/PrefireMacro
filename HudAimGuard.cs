using System;
using System.Drawing;

namespace PrefireMacro
{
    internal static class HudAimGuard
    {
        internal static bool HudNearAim(Rectangle hud, Rectangle area, Size client)
        {
            double aimX = area.X + client.Width * .45, aimY = area.Y + client.Height * .35;
            double offsetY = hud.Y + hud.Height * .5 - aimY;
            return !hud.IsEmpty && area.Contains(hud) && Math.Abs(hud.X + hud.Width * .5 - aimX) <= client.Width * .22 &&
                offsetY >= -client.Height * .20 && offsetY <= client.Height * .43;
        }
        // Small aim corrections must not erase a previously observed target.
        // Eligibility depends on the verified HUD position, not mouse packets.
        internal static bool RequestConfirmation(PrefireGate gate, bool held, bool visible, bool stable,
            Rectangle hud, Rectangle area, Size client, long now)
        {
            if (!HudNearAim(hud, area, client) || (!visible && !stable)) { gate.Reset(); return false; }
            return gate.RequestFreshConfirmation(held, visible, stable, now);
        }
    }
}
