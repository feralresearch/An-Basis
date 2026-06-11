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

        internal const string StreamingAssetsRelativePath = "Basis/client_config.xml";

        public static BasisClientConfiguration Load(string streamingAssetsPath)
        {
            string path = Path.Combine(streamingAssetsPath, StreamingAssetsRelativePath);
            if (!File.Exists(path)) return new BasisClientConfiguration();
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
    }
}
