using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DraftingSuite
{
    [DataContract]
    internal sealed class DraftingSuiteSettings
    {
        private const string SettingsFolderName = "DraftingSuite";
        private const string SettingsFileName = "settings.json";

        [DataMember(Order = 1)]
        public bool ExtractCogoDisplayGraphics { get; set; } = true;

        [DataMember(Order = 2)]
        public bool ConvertTextToMleaders { get; set; } = true;

        [DataMember(Order = 3)]
        public bool FlattenAnnotation { get; set; } = true;

        [DataMember(Order = 4)]
        public bool RestyleCogoPoints { get; set; } = true;

        [DataMember(Order = 5)]
        public double MLeaderTextOffsetX { get; set; } = 15.0;

        [DataMember(Order = 6)]
        public double MLeaderTextOffsetY { get; set; } = 15.0;

        [DataMember(Order = 7)]
        public double FlattenElevation { get; set; } = 0.0;

        [DataMember(Order = 8)]
        public string CogoPointStyleName { get; set; } = "Standard";

        [DataMember(Order = 9)]
        public string CogoLabelStyleName { get; set; } = "Standard";

        [DataMember(Order = 10)]
        public List<string> ProtectedSourceLayerPatterns { get; set; } = new List<string> { "*pnt", "*pnts", "*node*" };

        [DataMember(Order = 11)]
        public List<string> ResultLayerPatterns { get; set; } = new List<string> { "*RNDM", "*NODE-TOPO*", "*TOPO-SPOT*" };

        [DataMember(Order = 12)]
        public List<string> AnnotationLayerPatterns { get; set; } = new List<string> { "*-ANNO*", "*-TEXT*", "*-A", "*0", "*IDEN*" };

        [DataMember(Order = 13)]
        public double TinyTextDeleteHeight { get; set; } = 1.0;

        [DataMember(Order = 14)]
        public int ExplodePassesBeforeBurst { get; set; } = 2;

        [DataMember(Order = 15)]
        public bool BurstInserts { get; set; } = true;

        [DataMember(Order = 16)]
        public int ExplodePassesAfterBurst { get; set; } = 1;

        [DataMember(Order = 17)]
        public int MaxAnonymousBurstPasses { get; set; } = 8;

        public static string SettingsPath
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Civil3D_Plugins", SettingsFolderName, SettingsFileName);
            }
        }

        public static DraftingSuiteSettings Load()
        {
            try
            {
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    DraftingSuiteSettings defaults = CreateDefault();
                    defaults.Save();
                    return defaults;
                }

                using (FileStream stream = File.OpenRead(path))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(DraftingSuiteSettings));
                    DraftingSuiteSettings settings = serializer.ReadObject(stream) as DraftingSuiteSettings;
                    return Normalize(settings);
                }
            }
            catch
            {
                return CreateDefault();
            }
        }

        public static DraftingSuiteSettings CreateDefault()
        {
            return new DraftingSuiteSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            DraftingSuiteSettings settings = Normalize(this);
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(DraftingSuiteSettings));
                serializer.WriteObject(stream, settings);
                string json = Encoding.UTF8.GetString(stream.ToArray());
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            }
        }

        private static DraftingSuiteSettings Normalize(DraftingSuiteSettings settings)
        {
            settings = settings ?? CreateDefault();
            if (string.IsNullOrWhiteSpace(settings.CogoPointStyleName))
                settings.CogoPointStyleName = "Standard";
            if (string.IsNullOrWhiteSpace(settings.CogoLabelStyleName))
                settings.CogoLabelStyleName = "Standard";
            if (settings.ProtectedSourceLayerPatterns == null || settings.ProtectedSourceLayerPatterns.Count == 0)
                settings.ProtectedSourceLayerPatterns = new List<string> { "*pnt", "*pnts", "*node*" };
            if (settings.ResultLayerPatterns == null || settings.ResultLayerPatterns.Count == 0)
                settings.ResultLayerPatterns = new List<string> { "*RNDM", "*NODE-TOPO*", "*TOPO-SPOT*" };
            if (settings.AnnotationLayerPatterns == null || settings.AnnotationLayerPatterns.Count == 0)
                settings.AnnotationLayerPatterns = new List<string> { "*-ANNO*", "*-TEXT*", "*-A", "*0", "*IDEN*" };
            if (settings.ExplodePassesBeforeBurst < 0)
                settings.ExplodePassesBeforeBurst = 0;
            if (settings.ExplodePassesAfterBurst < 0)
                settings.ExplodePassesAfterBurst = 0;
            if (settings.MaxAnonymousBurstPasses < 1)
                settings.MaxAnonymousBurstPasses = 1;
            if (settings.TinyTextDeleteHeight < 0.0)
                settings.TinyTextDeleteHeight = 0.0;
            return settings;
        }
    }
}
