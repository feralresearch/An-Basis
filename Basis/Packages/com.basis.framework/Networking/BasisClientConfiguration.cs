using System;
using System.IO;
using System.Xml.Serialization;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    [Serializable]
    public class BasisClientConfiguration
    {
        public string DeepLinkScheme = "basisdemo";
        /// <summary>
        /// When true, only one instance of the client can run at a time on Windows.
        /// A second launch forwards its deep link URL to the running instance and quits.
        /// Leave false (default) to allow multiple clients, e.g. for local multiplayer testing.
        /// </summary>
        public bool SingleInstance = false;

        public static BasisClientConfiguration Load()
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
                return WriteDefaults(path);
            try
            {
                var serializer = new XmlSerializer(typeof(BasisClientConfiguration));
                using (var reader = new StreamReader(path))
                    return (BasisClientConfiguration)serializer.Deserialize(reader);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BasisClientConfiguration] Failed to load {path}: {ex.Message}");
                return new BasisClientConfiguration();
            }
        }

        private static BasisClientConfiguration WriteDefaults(string path)
        {
            var config = new BasisClientConfiguration();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var serializer = new XmlSerializer(typeof(BasisClientConfiguration));
                using (var writer = new StreamWriter(path))
                    serializer.Serialize(writer, config);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BasisClientConfiguration] Could not write default config to {path}: {ex.Message}");
            }
            return config;
        }

        public static string GetConfigPath()
        {
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX
            // Editor: project root. Standalone: directory containing the exe.
            return Path.Combine(Path.GetDirectoryName(Application.dataPath), "config", "client_config.xml");
#elif UNITY_STANDALONE_OSX
            // dataPath = Game.app/Contents/Data — go up three levels to reach the .app's parent.
            return Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath))),
                "config", "client_config.xml");
#else
            return Path.Combine(Application.persistentDataPath, "config", "client_config.xml");
#endif
        }
    }
}
