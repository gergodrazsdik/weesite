using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Weesite.Search;

/// <summary>
/// Turns off ONNX Runtime's telemetry (F-0261). Its official builds (1.32 here) upload events
/// to Microsoft (1DS, <c>mobile.events.data.microsoft.com</c>) on Linux too, on by default: a
/// process-info event with a device ID, the OS, the CPU and the memory, as soon as the first
/// <see cref="OrtEnv"/> is created. <see cref="OrtEnv.DisableTelemetryEvents"/> comes too late
/// for that event; only <c>ORT_DISABLE_TELEMETRY=1</c> in the native environment before then
/// stops the uploader (the package's Privacy.md, "Disabling Telemetry").
/// </summary>
internal static partial class OnnxTelemetry
{
    /// <summary>The variable ONNX Runtime reads when it creates its environment.</summary>
    internal const string Variable = "ORT_DISABLE_TELEMETRY";

    private static readonly Lock Gate = new();
    private static bool _disabled;

    /// <summary>
    /// Turns telemetry off for the process, once; call before anything touches ONNX Runtime.
    /// </summary>
    public static void Disable()
    {
        lock (Gate)
        {
            if (_disabled)
            {
                return;
            }

            // On Linux and macOS, Environment.SetEnvironmentVariable changes only .NET's own
            // copy, which native code doesn't see: setenv changes the one ONNX Runtime reads.
            // The image sets the variable already (Containerfile), so there it's left alone.
            if (!OperatingSystem.IsWindows() && NativeValue() != "1")
            {
                _ = SetEnv(Variable, "1", 1);
            }

            Environment.SetEnvironmentVariable(Variable, "1");

            // The rest of its events, and the only switch for the Windows ETW builds.
            OrtEnv.Instance().DisableTelemetryEvents();
            _disabled = true;
        }
    }

    /// <summary>Gets the variable as native code sees it (on Linux and macOS).</summary>
    /// <returns>The value, or null when it isn't set.</returns>
    internal static string? NativeValue() => Marshal.PtrToStringUTF8(GetEnv(Variable));

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetEnv(string name, string value, int overwrite);

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint GetEnv(string name);
}
