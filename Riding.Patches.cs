using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// The hooks into the game's character: your movement input goes to the horse, your character goes where the rider
    /// is, and you stay "on the ground"; and the rider animation is put on your body for each camera that draws. Each is a prefix that does nothing (one flag test) unless it's you, riding.
    /// </summary>
    internal static partial class Riding
    {
        private static Vector2 input;
        // Walking up to the horse: the way your legs are told to walk (the mod does the moving).
        private static Vector2 approach;
        private static float inputAt;
        /// <summary>When sprint was last switched on.</summary>
        internal static float SprintSince { get; private set; }

        /// <summary>The movement keys as the game read them (x right, y forward), only while they're current.</summary>
        internal static Vector2 Steering => Override ?? (Time.time - inputAt < .25f ? input : Vector2.zero);
        /// <summary>The self test's hands on the reins, in place of the keys.</summary>
        internal static Vector2? Override;

        internal static void Patch(Harmony harmony)
        {
            void Prefix(Type type, string method, string patch, params Type[] args)
            {
                var target = args.Length > 0 ? AccessTools.Method(type, method, args) : AccessTools.Method(type, method);
                if (target == null) { Plugin.Log.LogError("Not found to patch: " + type.Name + "." + method); return; }
                try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(Riding), patch)); }
                catch (Exception e) { Plugin.Log.LogError($"Patching {type.Name}.{method} failed: {e.Message}"); }
            }
            Prefix(typeof(Player), nameof(Player.Move), nameof(MovePrefix), typeof(Vector2));
            Prefix(typeof(Player), nameof(Player.EnableSprint), nameof(EnableSprintPrefix), typeof(bool));
            Prefix(typeof(Player), nameof(Player.ToggleSprint), nameof(ToggleSprintPrefix));
            Prefix(typeof(Player), nameof(Player.Jump), nameof(JumpPrefix));
            Prefix(typeof(Player), nameof(Player.Vaulting), nameof(BlockPrefix));
            Prefix(typeof(Player), nameof(Player.EnableVaulting), nameof(BlockPrefix));
            Prefix(typeof(Player), nameof(Player.ChangePose), nameof(BlockPrefix), typeof(float));
            Prefix(typeof(Player), nameof(Player.TogglePose), nameof(BlockPrefix));
            Prefix(typeof(Player), nameof(Player.ToggleProne), nameof(BlockPrefix));
            Prefix(typeof(MovementContext), nameof(MovementContext.DirectApplyMotion), nameof(PlacePrefix), typeof(Vector3), typeof(float));
            Prefix(typeof(MovementContext), nameof(MovementContext.UpdateGroundCollision), nameof(GroundPrefix), typeof(float));
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
        }

        private static bool Mine(Player player) => active && ReferenceEquals(player, Rider);

        private static void MovePrefix(Player __instance, ref Vector2 direction)
        {
            if (!Mine(__instance)) return;
            if (phase == Phase.Approach) { direction = approach; return; }
            input = direction; inputAt = Time.time;
            direction = Vector2.zero;
        }

        private static bool EnableSprintPrefix(Player __instance, bool enable)
        {
            if (!Mine(__instance)) return true;
            Sprint = enable;
            if (enable) SprintSince = Time.time;
            return false;
        }

        private static bool ToggleSprintPrefix(Player __instance)
        {
            if (!Mine(__instance)) return true;
            Sprint = !Sprint;
            if (Sprint) SprintSince = Time.time;
            return false;
        }

        private static bool JumpPrefix(Player __instance)
        {
            if (!Mine(__instance)) return true;
            JumpQueued = true;
            return false;
        }

        // Crouching, going prone and vaulting don't apply in the saddle.
        private static bool BlockPrefix(Player __instance) => !Mine(__instance);

        // Where the game moves your character each frame: on the horse, it's put where the rider animation has it.
        private static bool PlacePrefix(MovementContext __instance)
        {
            if (!active || !ReferenceEquals(__instance, context)) return true;
            try
            {
                // The horse went away under you (it should have let go first).
                if (Horse == null) { Release(); return true; }
                __instance.TransformPosition = Placement();
                if (phase == Phase.Dismounting && Time.time - phaseStart >= phaseLength) Finish();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Riding stopped: " + e);
                Release();
            }
            return false;
        }

        // On the horse you're on the ground as far as the game's concerned (no falling, no fall damage).
        private static bool GroundPrefix(MovementContext __instance)
        {
            if (!active || !ReferenceEquals(__instance, context)) return true;
            __instance.IsGrounded = true;
            __instance.FreefallTime = 0;
            __instance.ResetFlying();
            return false;
        }
    }
}
