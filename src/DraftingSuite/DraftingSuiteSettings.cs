using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Linq;
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

        [DataMember(Order = 18)]
        public string PresetName { get; set; } = string.Empty;

        [DataMember(Order = 19)]
        public string PresetFolderPath { get; set; } = string.Empty;

        [DataMember(Order = 20)]
        public string DefaultPresetName { get; set; } = string.Empty;

        [DataMember(Order = 21)]
        public List<string> MLeaderIgnoreLayerPatterns { get; set; } = new List<string> { "*-PNT" };

        [DataMember(Order = 22)]
        public List<string> MLeaderDeleteLayerPatterns { get; set; } = new List<string> { "*-PNT" };

        [DataMember(Order = 23)]
        public List<string> MLeaderKeepTextLayerPatterns { get; set; } = new List<string>();

        public static string SettingsPath
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Civil3D_Plugins", SettingsFolderName, SettingsFileName);
            }
        }

        public static string DefaultPresetFolderPath
        {
            get
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Civil3D_Plugins", SettingsFolderName, "Presets");
            }
        }

        public static DraftingSuiteSettings Load()
        {
            DraftingSuiteSettings active = LoadActiveSettings();
            if (!string.IsNullOrWhiteSpace(active.DefaultPresetName))
            {
                DraftingSuiteSettings preset = LoadPreset(active.DefaultPresetName, active.PresetFolderPath);
                if (preset != null)
                {
                    preset.PresetFolderPath = active.PresetFolderPath;
                    preset.DefaultPresetName = active.DefaultPresetName;
                    preset.PresetName = active.DefaultPresetName;
                    return Normalize(preset);
                }
            }

            return active;
        }

        public static DraftingSuiteSettings LoadActiveSettings()
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

        public static string[] ListPresetNames(string folderPath)
        {
            try
            {
                folderPath = NormalizePresetFolderPath(folderPath);
                if (!Directory.Exists(folderPath))
                    return new string[0];

                return Directory.GetFiles(folderPath, "*.json")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        public static DraftingSuiteSettings LoadPreset(string presetName, string folderPath)
        {
            try
            {
                string path = GetPresetPath(presetName, folderPath);
                if (!File.Exists(path))
                    return null;

                using (FileStream stream = File.OpenRead(path))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(DraftingSuiteSettings));
                    DraftingSuiteSettings settings = serializer.ReadObject(stream) as DraftingSuiteSettings;
                    settings = Normalize(settings);
                    settings.PresetName = Path.GetFileNameWithoutExtension(path);
                    settings.PresetFolderPath = NormalizePresetFolderPath(folderPath);
                    return settings;
                }
            }
            catch
            {
                return null;
            }
        }

        public void SavePreset(string presetName)
        {
            string path = GetPresetPath(presetName, PresetFolderPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            DraftingSuiteSettings settings = Normalize(this);
            settings.PresetName = Path.GetFileNameWithoutExtension(path);
            settings.WriteToPath(path);
        }

        public static bool DeletePreset(string presetName, string folderPath)
        {
            try
            {
                string path = GetPresetPath(presetName, folderPath);
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool RenamePreset(string oldName, string newName, string folderPath)
        {
            try
            {
                string oldPath = GetPresetPath(oldName, folderPath);
                string newPath = GetPresetPath(newName, folderPath);
                if (!File.Exists(oldPath) || File.Exists(newPath))
                    return false;

                File.Move(oldPath, newPath);
                DraftingSuiteSettings renamed = LoadPreset(newName, folderPath);
                if (renamed != null)
                    renamed.SavePreset(newName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
            DraftingSuiteSettings settings = Normalize(this);
            settings.WriteToPath(SettingsPath);
        }

        private void WriteToPath(string path)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(DraftingSuiteSettings));
                serializer.WriteObject(stream, this);
                string json = Encoding.UTF8.GetString(stream.ToArray());
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }

        private static string GetPresetPath(string presetName, string folderPath)
        {
            string cleanName = SanitizePresetName(presetName);
            return Path.Combine(NormalizePresetFolderPath(folderPath), cleanName + ".json");
        }

        private static string SanitizePresetName(string presetName)
        {
            string cleanName = (presetName ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                cleanName = cleanName.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(cleanName) ? "Preset" : cleanName;
        }

        private static string NormalizePresetFolderPath(string folderPath)
        {
            return string.IsNullOrWhiteSpace(folderPath) ? DefaultPresetFolderPath : folderPath.Trim();
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
            if (settings.MLeaderIgnoreLayerPatterns == null)
                settings.MLeaderIgnoreLayerPatterns = new List<string>();
            if (settings.MLeaderDeleteLayerPatterns == null || settings.MLeaderDeleteLayerPatterns.Count == 0)
                settings.MLeaderDeleteLayerPatterns = settings.MLeaderIgnoreLayerPatterns.Count > 0
                    ? new List<string>(settings.MLeaderIgnoreLayerPatterns)
                    : new List<string> { "*-PNT" };
            if (settings.MLeaderKeepTextLayerPatterns == null)
                settings.MLeaderKeepTextLayerPatterns = new List<string>();
            if (settings.ExplodePassesBeforeBurst < 0)
                settings.ExplodePassesBeforeBurst = 0;
            if (settings.ExplodePassesAfterBurst < 0)
                settings.ExplodePassesAfterBurst = 0;
            if (settings.MaxAnonymousBurstPasses < 1)
                settings.MaxAnonymousBurstPasses = 1;
            if (settings.TinyTextDeleteHeight < 0.0)
                settings.TinyTextDeleteHeight = 0.0;
            settings.PresetName = settings.PresetName ?? string.Empty;
            settings.PresetFolderPath = NormalizePresetFolderPath(settings.PresetFolderPath);
            settings.DefaultPresetName = settings.DefaultPresetName ?? string.Empty;
            return settings;
        }
    }
}
