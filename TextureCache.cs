using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace TushonkaHorse
{
    /// <summary>
    /// Pictures decoded and compressed once, then kept twice over: in memory for the session (later raids reuse the
    /// same texture) and on disk (cache folder next to the mod) in the graphics card's own format, so the next game
    /// start loads them straight back.
    /// </summary>
    internal static class TextureCache
    {
        private static readonly Dictionary<string, Texture2D> loaded = new Dictionary<string, Texture2D>();
        private static string Folder => Path.Combine(Plugin.AssetFolder, "cache");

        // Linear for data (the normal map), sRGB for colour.
        internal static Texture2D Load(string path, bool linear)
        {
            string key = path + (linear ? "|linear" : "");
            if (loaded.TryGetValue(key, out Texture2D texture) && texture != null) return texture;
            string name = Path.GetFileName(path);
            var source = new FileInfo(path);
            string stamp = ((source.LastWriteTimeUtc.Ticks ^ source.Length) + (linear ? 1 : 0)).ToString("x");
            texture = ReadBack(name, stamp, linear);
            if (texture == null)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
                {
                    UnityEngine.Object.Destroy(texture);
                    throw new InvalidDataException("Cannot decode " + name);
                }
                texture.Compress(true);
                Keep(name, stamp, texture);
                // On the graphics card only, with no copy kept in RAM.
                texture.Apply(false, true);
            }
            texture.name = name;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.anisoLevel = 4;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return loaded[key] = texture;
        }

        // <picture>.<stamp>.<width>.<height>.<format>.tex
        private static Texture2D ReadBack(string name, string stamp, bool linear)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;
                string file = Directory.GetFiles(Folder, name + "." + stamp + ".*.tex").FirstOrDefault();
                if (file == null) return null;
                string[] parts = Path.GetFileName(file).Substring(name.Length + stamp.Length + 2).Split('.');
                var texture = new Texture2D(int.Parse(parts[0]), int.Parse(parts[1]), (TextureFormat)int.Parse(parts[2]), true, linear);
                texture.LoadRawTextureData(File.ReadAllBytes(file));
                texture.Apply(false, true);
                return texture;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("A kept texture of " + name + " couldn't be read; it's made again: " + e.Message);
                return null;
            }
        }

        private static void Keep(string name, string stamp, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (string old in Directory.GetFiles(Folder, name + ".*.tex")) File.Delete(old);
                File.WriteAllBytes(Path.Combine(Folder, $"{name}.{stamp}.{texture.width}.{texture.height}.{(int)texture.format}.tex"), texture.GetRawTextureData());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't keep the texture of " + name + ": " + e.Message); }
        }
    }
}
