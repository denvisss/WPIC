using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WPIC.Services;

public interface ISystemParameters
{
    string Read(string parameter);
    void Write(string parameter, string state);
}

public sealed class WindowsSystemParameters : ISystemParameters
{
    private static readonly uint[] AnimationGetters = [0x1042, 0x1002, 0x1004, 0x1016, 0x1006, 0x1014];
    private const uint PersistAndNotify = 3;

    public string Read(string parameter)
    {
        int[] values;
        if (parameter == "animations")
        {
            values = new int[AnimationGetters.Length + 1];
            for (var i = 0; i < AnimationGetters.Length; i++)
                Check(SystemParametersInfoValue(AnimationGetters[i], 0, ref values[i], 0));
            var animation = new[] { 8, 0 };
            Check(SystemParametersInfoArray(0x48, 8, animation, 0));
            values[^1] = animation[1];
        }
        else
        {
            var (getter, size) = AccessibilityParameter(parameter);
            var data = new int[size / 4];
            data[0] = size;
            Check(SystemParametersInfoArray(getter, (uint)size, data, 0));
            values = data[1..];
        }
        return JsonSerializer.Serialize(values);
    }

    public static string DisabledState(string parameter, string state)
    {
        var values = Parse(parameter, state);
        if (parameter == "animations") Array.Clear(values);
        else values[0] &= ~0x4; // HOTKEYACTIVE only; preserve the feature, feedback and timing flags.
        return JsonSerializer.Serialize(values);
    }

    public static bool IsDisabled(string parameter, string state)
    {
        var values = Parse(parameter, state);
        return parameter == "animations" ? values.All(v => v == 0) : (values[0] & 0x4) == 0;
    }

    public void Write(string parameter, string state)
    {
        var values = Parse(parameter, state);
        if (parameter == "animations")
        {
            for (var i = 0; i < AnimationGetters.Length; i++)
                Check(SystemParametersInfoPointer(AnimationGetters[i] + 1, 0, (IntPtr)values[i], PersistAndNotify));
            var animation = new[] { 8, values[^1] };
            Check(SystemParametersInfoArray(0x49, 8, animation, PersistAndNotify));
        }
        else
        {
            var (getter, size) = AccessibilityParameter(parameter);
            var data = new int[size / 4];
            data[0] = size;
            values.CopyTo(data, 1);
            Check(SystemParametersInfoArray(getter + 1, (uint)size, data, PersistAndNotify));
        }
    }

    private static int[] Parse(string parameter, string state)
    {
        var length = parameter == "animations" ? AnimationGetters.Length + 1 : AccessibilityParameter(parameter).Size / 4 - 1;
        var values = JsonSerializer.Deserialize<int[]>(state);
        if (values == null || values.Length != length ||
            (parameter == "animations" && values.Any(v => v is not (0 or 1))))
            throw new InvalidDataException(L10n.T("tweak.err.invalidSnapshot"));
        return values;
    }

    private static (uint Getter, int Size) AccessibilityParameter(string parameter) => parameter switch
    {
        "stickyKeysHotkey" => (0x3A, 8),
        "filterKeysHotkey" => (0x32, 24),
        _ => throw new ArgumentException(L10n.T("tweak.err.parameter", parameter))
    };

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoValue(uint action, uint parameter, ref int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoPointer(uint action, uint parameter, IntPtr value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoArray(uint action, uint parameter, [In, Out] int[] values, uint flags);
}
