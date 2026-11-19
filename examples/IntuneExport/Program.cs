// Turns the AI Agent Blocklist into files a Windows endpoint team can hand to Intune or PowerShell.
//
//   dotnet run --project examples/IntuneExport -- <aab_full.json or "download"> <output folder> [minimum grade]
//
// Writes:
//   windows_agents.csv           kind, ident, name, agent_type, risk_grade for Windows-side identifiers
//   Set-ExtensionBlocklist.ps1   a remediation script that fills the Chrome and Edge ExtensionInstallBlocklist keys
//   edge_extension_ids.txt       one Edge extension ID per line, for an Intune settings catalog list
//
// "download" fetches aab_full.json with the token in AIAGENTBLOCKLIST_TOKEN.
// The optional minimum grade (Low, Moderate, Elevated, High, Severe) keeps only entries at or above it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AiAgentBlocklist;

namespace IntuneExport
{
    public static class Program
    {
        private static readonly string[] Grades = { "Low", "Moderate", "Elevated", "High", "Severe" };
        private static readonly string[] WindowsKinds = { "process_win", "winget", "ms_store", "process_cli" };

        public static async Task<int> Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: IntuneExport <aab_full.json|download> <output folder> [minimum grade]");
                return 2;
            }

            Blocklist list;
            try
            {
                if (args[0] == "download")
                {
                    using var client = new AiAgentBlocklistClient();
                    list = await client.LoadBlocklistAsync();
                }
                else
                {
                    list = Blocklist.FromFile(args[0]);
                }
            }
            catch (AiAgentBlocklistException ex)
            {
                Console.Error.WriteLine("could not load the list: " + ex.Message);
                return 1;
            }

            var minimum = args.Length > 2 ? Array.FindIndex(Grades, g => g.Equals(args[2], StringComparison.OrdinalIgnoreCase)) : -1;
            bool Keep(Entry e) => minimum < 0 || Array.IndexOf(Grades, e.RiskGrade) >= minimum;

            var outDir = args[1];
            Directory.CreateDirectory(outDir);

            // 1. CSV of Windows-side identifiers.
            var csv = new StringBuilder("kind,ident,name,agent_type,risk_grade\n");
            var windowsRows = 0;
            foreach (var kind in WindowsKinds)
            {
                foreach (var e in list.Filter(kind: kind).Where(Keep))
                {
                    csv.Append(Csv(e.Kind)).Append(',').Append(Csv(e.Ident)).Append(',').Append(Csv(e.Name)).Append(',')
                        .Append(Csv(e.AgentType)).Append(',').Append(Csv(e.RiskGrade)).Append('\n');
                    windowsRows++;
                }
            }
            File.WriteAllText(Path.Combine(outDir, "windows_agents.csv"), csv.ToString());

            // 2. Edge IDs, one per line.
            var edge = list.Filter(kind: "edge_ext").Where(Keep).Select(e => e.Ident).Distinct().ToList();
            File.WriteAllLines(Path.Combine(outDir, "edge_extension_ids.txt"), edge);

            // 3. PowerShell remediation script for both browsers.
            var chrome = list.Filter(kind: "chrome_ext").Where(Keep).Select(e => e.Ident).Distinct().ToList();
            File.WriteAllText(Path.Combine(outDir, "Set-ExtensionBlocklist.ps1"), BuildScript(chrome, edge, list.Version));

            Console.WriteLine($"list version {list.Version}: {windowsRows} Windows rows, {chrome.Count} Chrome IDs, {edge.Count} Edge IDs -> {outDir}");
            return 0;
        }

        private static string Csv(string value) =>
            value.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        private static string BuildScript(IReadOnlyList<string> chrome, IReadOnlyList<string> edge, string version)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Generated from AI Agent Blocklist version " + version);
            sb.AppendLine("# Run as SYSTEM (Intune remediation or a scheduled task). Existing numbered values are overwritten.");
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            AppendKey(sb, "HKLM:\\SOFTWARE\\Policies\\Google\\Chrome\\ExtensionInstallBlocklist", chrome);
            AppendKey(sb, "HKLM:\\SOFTWARE\\Policies\\Microsoft\\Edge\\ExtensionInstallBlocklist", edge);
            return sb.ToString();
        }

        private static void AppendKey(StringBuilder sb, string key, IReadOnlyList<string> ids)
        {
            sb.AppendLine();
            sb.AppendLine("New-Item -Path '" + key + "' -Force | Out-Null");
            for (var i = 0; i < ids.Count; i++)
            {
                sb.AppendLine("Set-ItemProperty -Path '" + key + "' -Name '" + (i + 1) + "' -Value '" + ids[i].Replace("'", "''") + "'");
            }
        }
    }
}
