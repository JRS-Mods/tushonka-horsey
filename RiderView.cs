using System;
using EFT;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// What of your character is drawn. In first person the game draws your arms and legs and nothing else of you, so
    /// with your weapon put away (Riding) that's all there is to see of the climb; the only thing switched here is
    /// your legs, for the "hidden legs" setting.
    /// </summary>
    internal static class RiderView
    {
        private static bool legsHidden;

        internal static bool LegsShown => !legsHidden;

        internal static void Legs(Player player, bool show)
        {
            try
            {
                if (show == !legsHidden || player == null || player.PlayerBody?.BodySkins == null) return;
                if (player.PlayerBody.BodySkins.TryGetValue(EBodyModelPart.Feet, out var feet) && feet != null) feet.EnableRenderers(show);
                legsHidden = !show;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Rider's legs: " + e.Message); }
        }

        /// <summary>Everything back as the game had it (and nothing remembered from this ride).</summary>
        internal static void Restore(Player player)
        {
            Legs(player, true);
            legsHidden = false;
        }
    }
}
