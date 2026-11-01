using System.ComponentModel;
using System.Diagnostics;

namespace Labcoat;

/// <summary>Child process helpers.</summary>
internal static class ProcessUtil
{
    /// <summary>Kills the process and its children, best effort: it may have exited already.</summary>
    public static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            /* exited already (or its process tree only partly): nothing left to stop */
        }
    }
}
