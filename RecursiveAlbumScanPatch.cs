using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using SteamShelf.Media.Albums;

namespace Boxroom_MusicEX
{
    /// <summary>
    /// Replaces the single Directory.GetDirectories(root) call in BOXROOM's album scanner.
    /// The rest of BOXROOM's scanner is deliberately left intact, so album registration,
    /// saved spawn state, cover loading, and game events continue to work normally.
    /// </summary>
    [HarmonyPatch]
    internal static class RecursiveAlbumScanPatch
    {
        // Method handles make the transpiler precise: only the one-argument directory scan is
        // replaced, not unrelated file-system calls in the same game method.
        private static readonly MethodInfo ShallowDirectoryScan = AccessTools.Method(
            typeof(Directory), nameof(Directory.GetDirectories), new[] { typeof(string) });

        private static readonly MethodInfo RecursiveDirectoryScan = AccessTools.Method(
            typeof(RecursiveAlbumScanPatch), nameof(GetAlbumDirectories));

        private static MethodBase TargetMethod()
        {
            // ScanLibraryAsync is compiled as a small wrapper plus a generated state machine.
            // Its real body (including Directory.GetDirectories) lives in MoveNext, so patching
            // ScanLibraryAsync itself would compile successfully but change nothing at runtime.
            MethodInfo scanMethod = AccessTools.Method(
                typeof(AlbumLibrarySystem), nameof(AlbumLibrarySystem.ScanLibraryAsync));
            AsyncStateMachineAttribute stateMachine = scanMethod.GetCustomAttribute<AsyncStateMachineAttribute>();

            if (stateMachine == null)
            {
                // A clear failure here is safer than silently loading a mod that does nothing.
                throw new MissingMethodException("Could not find the async state machine for AlbumLibrarySystem.ScanLibraryAsync.");
            }

            return AccessTools.Method(stateMachine.StateMachineType, "MoveNext");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // This is an intentionally tiny IL edit: preserve every original instruction and
            // swap only the called method. Mutating the existing instruction also preserves any
            // branch labels and exception-block metadata attached to it.
            bool scanCallFound = false;

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(ShallowDirectoryScan))
                {
                    scanCallFound = true;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = RecursiveDirectoryScan;
                }
                else if (instruction.Calls(RecursiveDirectoryScan))
                {
                    // Harmony can run a transpiler again against already-patched IL.
                    scanCallFound = true;
                }

                yield return instruction;
            }

            if (!scanCallFound)
            {
                // This normally means a BOXROOM update changed the implementation we target.
                MelonLogger.Error("[MusicEX] Could not patch the album folder scan. The game method may have changed.");
            }
        }

        private static string[] GetAlbumDirectories(string sourceRoot)
        {
            // Use our own iterative traversal rather than SearchOption.AllDirectories. The .NET
            // convenience overload aborts the entire scan when it encounters one inaccessible
            // directory; this version logs the bad folder and continues with the rest.
            var results = new List<string>();
            var pending = new Stack<string>();
            pending.Push(sourceRoot);

            while (pending.Count > 0)
            {
                string parent = pending.Pop();
                string[] children;

                try
                {
                    // BOXROOM later decides whether each candidate actually contains audio.
                    // Returning every descendant keeps that responsibility in the base game.
                    children = Directory.GetDirectories(parent);
                }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"[MusicEX] Skipping unreadable music folder '{parent}': {exception.Message}");
                    continue;
                }

                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                foreach (string child in children)
                {
                    // Every child is a candidate album, including folders that also have their
                    // own nested albums. BOXROOM ignores candidates with no supported tracks.
                    results.Add(child);

                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(child);
                        }
                        // Do not recurse through junctions/symbolic links. They can point back to
                        // an ancestor and create an infinite directory loop.
                    }
                    catch (Exception exception)
                    {
                        MelonLogger.Warning($"[MusicEX] Cannot inspect music folder '{child}': {exception.Message}");
                    }
                }
            }

            MelonLogger.Msg($"[MusicEX] Recursively queued {results.Count} folders for BOXROOM's album scan.");
            return results.ToArray();
        }
    }
}
