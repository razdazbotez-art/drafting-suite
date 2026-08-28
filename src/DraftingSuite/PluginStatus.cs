using System;
using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.InteropServices;
#endif

namespace DraftingSuite
{
    public static class PluginStatus
    {
        public static string TargetFramework
        {
            get
            {
#if NETFRAMEWORK
                return "net48";
#elif NET10_0_OR_GREATER
                return "net10.0-windows";
#else
                return "unknown";
#endif
            }
        }

        public static string RuntimeDescription
        {
            get
            {
#if NETFRAMEWORK
                return ".NET Framework CLR " + Environment.Version;
#else
                return RuntimeInformation.FrameworkDescription;
#endif
            }
        }

        public static string AssemblyPath
        {
            get
            {
                return Assembly.GetExecutingAssembly().Location ?? string.Empty;
            }
        }

        public static string ReadStatusJson()
        {
            return "{" +
                   "\"pluginId\":\"draftingsuite\"," +
                   "\"displayName\":\"Drafting Suite\"," +
                   "\"version\":\"" + Escape(Commands.VersionText) + "\"," +
                   "\"targetFramework\":\"" + Escape(TargetFramework) + "\"," +
                   "\"runtime\":\"" + Escape(RuntimeDescription) + "\"," +
                   "\"assemblyPath\":\"" + Escape(AssemblyPath) + "\"," +
                   "\"isLoaded\":true," +
                   "\"primaryCommand\":\"DS\"," +
                   "\"paletteCommand\":\"DS\"," +
                   "\"paletteSetName\":\"" + Escape(DraftingSuitePalette.StatusPaletteSetName) + "\"," +
                   "\"paletteSetGuid\":\"" + Escape(DraftingSuitePalette.StatusPaletteSetGuid) + "\"," +
                   "\"supportsPaletteEmbedding\":false," +
                   "\"status\":\"ok\"," +
                   "\"summary\":\"Loaded\"" +
                   "}";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }
    }
}
