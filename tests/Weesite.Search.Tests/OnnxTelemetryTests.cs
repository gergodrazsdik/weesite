using TUnit.Core.Enums;

namespace Weesite.Search.Tests;

/// <summary>ONNX Runtime's telemetry stays off (F-0261).</summary>
internal sealed class OnnxTelemetryTests
{
    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task The_switch_is_in_the_environment_native_code_reads()
    {
        OnnxTelemetry.Disable();

        // Not .NET's own copy of the environment: the one ONNX Runtime reads with getenv.
        await Assert.That(OnnxTelemetry.NativeValue()).IsEqualTo("1");
        await Assert.That(Environment.GetEnvironmentVariable(OnnxTelemetry.Variable)).IsEqualTo("1");
    }

    [Test]
    public async Task Turning_it_off_twice_is_harmless()
    {
        OnnxTelemetry.Disable();
        OnnxTelemetry.Disable();

        await Assert.That(Environment.GetEnvironmentVariable(OnnxTelemetry.Variable)).IsEqualTo("1");
    }
}
