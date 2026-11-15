using System;
using System.Collections;
using System.IO;
using System.Text;
using EFT;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// The self test (5. Debug > Self test; only exists while that's on). A few seconds into the raid, and again
    /// whenever a file called "test.go" appears in the mod's folder, it brings the horse beside you, gets on, rides a
    /// little way, looks down, and gets off, with nobody at the keys. Along the way it saves what you see and a view
    /// from beside the horse to the "test" folder, with a log of where your hands and feet were against where the
    /// animation wanted them.
    /// </summary>
    internal sealed class HorseTest : MonoBehaviour
    {
        internal Horse Horse;
        private GameWorld world;
        private string folder, trigger;
        private bool running, ranOnce;
        private float nextLook, startAt;
        private int shot;
        private Camera side;
        private RenderTexture target;
        private Texture2D pixels;
        private readonly StringBuilder log = new StringBuilder();

        private void Awake()
        {
            world = GetComponent<GameWorld>();
            folder = Path.Combine(Plugin.AssetFolder, "test");
            trigger = Path.Combine(Plugin.AssetFolder, "test.go");
            startAt = Time.time + 12;
        }

        private void Update()
        {
            if (running || Horse == null || Time.time < nextLook) return;
            nextLook = Time.time + 1;
            bool asked = File.Exists(trigger);
            if (asked) { try { File.Delete(trigger); } catch { } }
            if (!asked && (ranOnce || Time.time < startAt)) return;
            Player player = world.MainPlayer;
            if (player == null || player.HealthController == null || !player.HealthController.IsAlive) return;
            ranOnce = true;
            StartCoroutine(Run(player));
        }

        private IEnumerator Run(Player player)
        {
            running = true;
            // Each profile its own folder: two games on one PC (a Fika test) share the plugin folder.
            string who = player.Profile?.Nickname;
            folder = Path.Combine(Plugin.AssetFolder, "test", string.IsNullOrEmpty(who) ? "player" : who);
            RiderRig.Measure = true;
            log.Length = 0; shot = 0;
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); Directory.CreateDirectory(folder); }
            catch (Exception e) { Plugin.Log.LogWarning("Self test folder: " + e.Message); }
            Note($"Self test, version {Plugin.Version}, Fika {(HorseNet.Online ? "online" : "off")}. In hands: {player.HandsController?.GetType().Name}, view {player.Rotation}.");
            if (Riding.Active) { Riding.Dismount(); yield return Until(() => !Riding.Active, 10); }
            Note("Horse beside you: " + Horse.Beside(player));
            // Your horse also shown as another player's would be (Fika's remote horse), beside it in another coat.
            Horse.Echo = 4f;
            yield return new WaitForSeconds(1.5f);
            yield return Shot("before");
            yield return EchoShot("echo_before");

            Note("Mount: " + Horse.TryMount(player));
            float t = Time.time;
            while (Riding.Active && !Riding.Seated && Time.time - t < 12) { yield return Shot("mount"); yield return new WaitForSeconds(.2f); }
            Note($"In the saddle after {Time.time - t:0.0} s.");
            for (int i = 0; i < 4; i++) { yield return new WaitForSeconds(.5f); yield return Shot("seated"); }

            // Looking down either way (whichever way the game counts pitch), then level again.
            Vector2 level = player.Rotation;
            foreach (float pitch in new[] { 35f, 60f, -30f })
            {
                player.Rotation = new Vector2(level.x, pitch);
                yield return new WaitForSeconds(.4f);
                yield return Shot("pitch" + pitch.ToString("+0;-0"));
            }
            player.Rotation = new Vector2(level.x, 30);

            Riding.Override = new Vector2(0, 1);
            for (int i = 0; i < 5; i++) { yield return new WaitForSeconds(.5f); yield return Shot("riding"); }
            Riding.Override = null;
            yield return EchoShot("echo_riding");
            yield return new WaitForSeconds(2f);
            yield return Shot("stopped");
            player.Rotation = level;

            Note("Dismount.");
            Riding.Dismount();
            t = Time.time;
            while (Riding.Active && Time.time - t < 12) { yield return Shot("dismount"); yield return new WaitForSeconds(.2f); }
            Note($"On the ground after {Time.time - t:0.0} s.");
            for (int i = 0; i < 3; i++) { yield return new WaitForSeconds(.7f); yield return Shot("after"); }
            yield return EchoShot("echo_after");
            Horse.Echo = null;
            HorseNet.Echo(null);
            Note("Done.");
            RiderRig.Measure = false;
            Riding.Override = null;
            if (side != null) Destroy(side.gameObject);
            if (target != null) Destroy(target);
            if (pixels != null) Destroy(pixels);
            side = null; target = null; pixels = null;
            running = false;
        }

        private static IEnumerator Until(Func<bool> done, float seconds)
        {
            float t = Time.time;
            while (!done() && Time.time - t < seconds) yield return null;
        }

        private void Note(string text)
        {
            log.AppendLine($"{Time.time:0.00} {text}");
            Plugin.Log.LogInfo("Self test: " + text);
            try { File.WriteAllText(Path.Combine(folder, "log.txt"), log.ToString()); } catch { }
        }

        // What you see (the game's own screenshot) and the view from beside the horse, with the numbers for this moment.
        private IEnumerator Shot(string label)
        {
            string name = $"{shot++:00}_{label}";
            Player player = world.MainPlayer;
            HorseReplica echo = HorseNet.EchoReplica;
            string echoLine = echo == null ? "echo none" : $"echo {echo.Clip} at {echo.transform.position} seen {echo.Seen}";
            Note($"{name}: {echoLine} | remote {HorseNet.RemoteReport} | {Riding.State} hands {(player.HandsIsEmpty ? "empty" : player.HandsController?.GetType().Name)} you {player.Position} view {player.Rotation} | {Horse.DebugLine()} seen {Horse.Seen} | {Riding.Report}");
            try { ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + "_view.png")); }
            catch (Exception e) { Note("Screenshot: " + e.Message); }
            yield return new WaitForEndOfFrame();
            try { Beside(name, Horse.Body); }
            catch (Exception e) { Note("Side view: " + e.Message); }
        }

        // The view from beside the echo (the stand-in remote horse) only.
        private IEnumerator EchoShot(string label)
        {
            string name = $"{shot++:00}_{label}";
            HorseReplica echo = HorseNet.EchoReplica;
            Note($"{name}: " + (echo == null ? "no echo horse" : $"echo {echo.Clip} at {echo.transform.position} seen {echo.Seen}; yours at {Horse.Body.position} seen {Horse.Seen}"));
            yield return new WaitForEndOfFrame();
            if (echo == null) yield break;
            try { Beside(name, echo.transform); }
            catch (Exception e) { Note("Echo view: " + e.Message); }
        }

        private void Beside(string name, Transform horse)
        {
            Camera main = Camera.main;
            if (main == null) return;
            if (side == null)
            {
                side = new GameObject("Tushonka test camera").AddComponent<Camera>();
                // Its own plain camera: the game's has a whole pipeline hung on it that a copy doesn't get.
                side.enabled = false;
                side.cullingMask = main.cullingMask; side.clearFlags = CameraClearFlags.Skybox; side.farClipPlane = 400;
                side.renderingPath = RenderingPath.Forward;
                target = new RenderTexture(960, 540, 24);
                pixels = new Texture2D(960, 540, TextureFormat.RGB24, false);
                side.targetTexture = target;
                side.fieldOfView = 50; side.nearClipPlane = .05f; side.allowHDR = false;
            }
            // Front quarter, on the side you are (or were) on.
            Vector3 you = world.MainPlayer.Position - horse.position;
            float sign = Vector3.Dot(you, horse.right) >= 0 ? 1 : -1;
            Vector3 look = horse.position + Vector3.up * 1.3f;
            side.transform.position = look + horse.forward * 2.2f + horse.right * (3.2f * sign) + Vector3.up * .5f;
            side.transform.LookAt(look);
            side.Render();
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
            RenderTexture.active = was;
            File.WriteAllBytes(Path.Combine(folder, name + "_side.jpg"), pixels.EncodeToJPG(85));
        }

        private void OnDestroy()
        {
            if (Horse != null) Horse.Echo = null;
            HorseNet.Echo(null);
            RiderRig.Measure = false;
            Riding.Override = null;
            if (side != null) Destroy(side.gameObject);
        }
    }
}
